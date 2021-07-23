using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Bloom;
using Bloom.Api;
using Bloom.Book;
using BloomHarvester.LogEntries;
using CoenM.ImageHash.HashAlgorithms;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SIL.Xml;

namespace BloomHarvester
{
	internal interface IBookAnalyzer
	{
		string WriteBloomCollection(string bookFolder);

		bool IsBloomReaderSuitable(List<LogEntry> harvestLogEntries);
		bool IsEpubSuitable(List<LogEntry> harvestLogEntries);

		int GetBookComputedLevel();

		string GetBestPHashImageSource();
		ulong ComputeImageHash(string path);
	}

	/// <summary>
	/// Analyze a book and extract various information the harvester needs
	/// </summary>
	class BookAnalyzer : IBookAnalyzer
	{
		private HtmlDom _dom;
		private string _bookDirectory;

		public BookAnalyzer(string html, string meta, string bookDirectory = "")
		{
			_bookDirectory = bookDirectory;
			_dom = new HtmlDom(XmlHtmlConverter.GetXmlDomFromHtml(html, false));
			Language1Code = GetBestLangCode(1) ?? "";
			Language2Code = GetBestLangCode(2) ?? "en";
			Language3Code = GetBestLangCode(3) ?? "";

			var metaObj = DynamicJson.Parse(meta);
			if (metaObj.IsDefined("brandingProjectName"))
			{
				this.Branding = metaObj.brandingProjectName;
			}
			else
			{
				// If we don't set this default value, then the epub will not build successfully. (The same is probably true for the
				// bloomd file.)  We get a "Failure to completely load visibility document in RemoveUnwantedContent" exception thrown.
				// See https://issues.bloomlibrary.org/youtrack/issue/BL-8485.
				this.Branding = "Default";
			}

			var signLanguageCode = GetSignLanguageCode(metaObj);

			string pageNumberStyle = null;
			if (metaObj.IsDefined("page-number-style"))
			{
				pageNumberStyle = metaObj["page-number-style"];
			}

			bool isRtl = false;
			if (metaObj.IsDefined("isRtl"))
			{
				isRtl = metaObj["isRtl"];
			}

			var bloomCollectionElement =
				new XElement("Collection",
					new XElement("Language1Iso639Code", new XText(Language1Code)),
					new XElement("Language2Iso639Code", new XText(Language2Code)),
					new XElement("Language3Iso639Code", new XText(Language3Code)),
					new XElement("SignLanguageIso639Code", new XText(signLanguageCode)),
					new XElement("Language1Name", new XText(GetLanguageDisplayNameOrEmpty(metaObj, Language1Code))),
					new XElement("Language2Name", new XText(GetLanguageDisplayNameOrEmpty(metaObj, Language2Code))),
					new XElement("Language3Name", new XText(GetLanguageDisplayNameOrEmpty(metaObj, Language3Code))),
					new XElement("XMatterPack", new XText(GetBestXMatter())),
					new XElement("BrandingProjectName", new XText(Branding ?? "")),
					new XElement("PageNumberStyle", new XText(pageNumberStyle ?? "")),
					new XElement("IsLanguage1Rtl"), new XText(isRtl.ToString().ToLowerInvariant())
					);
			var sb = new StringBuilder();
			using (var writer = XmlWriter.Create(sb))
				bloomCollectionElement.WriteTo(writer);
			BloomCollection = sb.ToString();
		}

		// The only trace in the book that it belongs to a collection with a sign language is that
		// it is marked as having the sign language feature for that language. This is unfortunate but
		// the best we can do with the data we're currently uploading. We really need to know this,
		// because if sign language of the collection is not set, updating the book's features will
		// remove the language-specific sign language feature, and then we have no way to know it
		// should be there.
		// This is not very reliable; the collection might have a sign language but if the book
		// doesn't have video it will not be reflected in features. However, for now, we only care
		// about it in order to preserve the language-specific feature, so getting it from the
		// existing one is good enough.
		private string GetSignLanguageCode(dynamic metaObj)
		{
			if (!metaObj.IsDefined("features"))
				return "";
			var features = metaObj.features.Deserialize<string[]>();
			foreach (string feature in features)
			{
				const string marker = "signLanguage:";
				if (feature.StartsWith(marker))
				{
					return feature.Substring(marker.Length);
				}
			}
			return "";
		}

		private string GetLanguageDisplayNameOrEmpty(dynamic metadata, string isoCode)
		{
			if (string.IsNullOrEmpty(isoCode))
				return "";

			if (metadata.IsDefined("language-display-names") && metadata["language-display-names"].IsDefined(isoCode))
				return metadata["language-display-names"][isoCode];

			return "";
		}

		/// <summary>
		/// Gets the language code for the specified language number
		/// </summary>
		/// <param name="x">The language number</param>
		/// <returns>The language code for the specified language, as determined from the bloomDataDiv. Returns null if not found.</returns>
		private string GetBestLangCode(int x)
		{
			string xpathString = $"//*[@id='bloomDataDiv']/*[@data-book='contentLanguage{x}']";
			var matchingNodes = _dom.SafeSelectNodes(xpathString);
			if (matchingNodes.Count == 0)
			{
				// contentLanguage2 and contentLanguage3 are only present in bilingual or trilingual books,
				// so we fall back to getting lang 2 and 3 from the html if needed.
				// We should never be missing contentLanguage1 (but having the fallback here is basically free).
				return GetLanguageCodeFromHtml(x);
			}
			var matchedNode = matchingNodes.Item(0);
			string langCode = matchedNode.InnerText.Trim();
			return langCode;
		}

		private string GetLanguageCodeFromHtml(int languageNumber)
		{
			string classToLookFor;
			switch (languageNumber)
			{
				case 1:
					classToLookFor = "bloom-content1";
					break;
				case 2:
					classToLookFor = "bloom-contentNational1";
					break;
				case 3:
					classToLookFor = "bloom-contentNational2";
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(languageNumber), "Must be 1, 2, or 3");
			}
			// We make the assumption that the bookTitle is always present and always has any relevant language
			string xpathString = $"//div[contains(@class, '{classToLookFor}') and @data-book='bookTitle' and @lang]";
			return _dom.SelectSingleNode(xpathString)?.Attributes["lang"]?.Value;
		}

		/// <summary>
		/// Finds the XMatterName for this book. If it cannot be determined, falls back to "Device"
		/// </summary>
		private string GetBestXMatter()
		{
			string xmatterName = "Device";	// This is the default, in case anything goes wrong.
			if (String.IsNullOrEmpty(_bookDirectory))
			{
				return xmatterName;
			}

			DirectoryInfo dirInfo;
			try
			{
				dirInfo = new DirectoryInfo(_bookDirectory);
			}
			catch
			{
				return xmatterName;
			}

			string suffix = "-XMatter.css";
			var files = dirInfo.GetFiles();
			var matches = files.Where(x => x.Name.EndsWith(suffix));

			if (matches.Any())
			{
				string xmatterFilename = matches.First().Name;
				xmatterName = XMatterHelper.GetXMatterFromStyleSheetFileName(xmatterFilename);
			}

			return xmatterName ?? "Device";
		}

		public static BookAnalyzer FromFolder(string bookFolder)
		{
			var filename = Path.GetFileName(bookFolder);
			var bookPath = Bloom.Book.BookStorage.FindBookHtmlInFolder(bookFolder);
			if (!File.Exists(bookPath))
				throw new Exception("Incomplete upload: missing book's HTML file");
			var metaPath = Path.Combine(bookFolder, "meta.json");
			if (!File.Exists(metaPath))
				throw new Exception("Incomplete upload: missing book's meta.json file");
			return new BookAnalyzer(File.ReadAllText(bookPath, Encoding.UTF8),
				File.ReadAllText(metaPath, Encoding.UTF8), bookFolder);
		}

		public string WriteBloomCollection(string bookFolder)
		{
			var collectionFolder = Path.GetDirectoryName(bookFolder);
			var result = Path.Combine(collectionFolder, "temp.bloomCollection");
			File.WriteAllText(result, BloomCollection, Encoding.UTF8);
			return result;
		}

		public string Language1Code { get;}
		public string Language2Code { get; }
		public string Language3Code { get; set; }
		public string Branding { get; }

		/// <summary>
		/// The content appropriate to a skeleton BookCollection file for this book.
		/// </summary>
		public string BloomCollection { get; set; }

		/// <summary>
		/// For now, we assume that generated Bloom Reader books are always suitable.
		/// </summary>
		public bool IsBloomReaderSuitable(List<LogEntry> harvestLogEntries)
		{
			return true;
		}

		/// <summary>
		/// Our simplistic check for ePUB suitability is that all of the content pages
		/// have 0 or 1 each of images, text boxes, and/or videos
		/// </summary>
		public bool IsEpubSuitable(List<LogEntry> harvestLogEntries)
		{
			int goodPages = 0;
			foreach (var div in GetNumberedPages().ToList())
			{
				var imageContainers = div.SafeSelectNodes("div[contains(@class,'marginBox')]//div[contains(@class,'bloom-imageContainer')]");
				if (imageContainers.Count > 1)
				{
					harvestLogEntries.Add(new LogEntry(LogLevel.Info, LogType.ArtifactSuitability, "Bad ePUB because some page(s) had multiple images"));
					return false;
				}
				// Count any translation group which is not an image description
				var translationGroups = GetTranslationGroupsFromPage(div, includeImageDescriptions: false);
				if (translationGroups.Count > 1)
				{
					harvestLogEntries.Add(new LogEntry(LogLevel.Info, LogType.ArtifactSuitability, "Bad ePUB because some page(s) had multiple text boxes"));
					return false;
				}
				var videos = div.SafeSelectNodes("following-sibling::div[contains(@class,'marginBox')]//video");
				if (videos.Count > 1)
				{
					harvestLogEntries.Add(new LogEntry(LogLevel.Info, LogType.ArtifactSuitability, "Bad ePUB because some page(s) had multiple videos"));
					return false;
				}
				++goodPages;
			}
			if (goodPages == 0)
				harvestLogEntries.Add(new LogEntry(LogLevel.Info, LogType.ArtifactSuitability, "Bad ePUB because there were no content pages"));
			return goodPages > 0;
		}

		/// <summary>
		/// Computes an estimate of the level of the book
		/// </summary>
		/// <returns>An int representing the level of the book.
		/// 1: "First words", 2: "First sentences", 3: "First paragraphs", 4: "Longer paragraphs"
		/// -1: Error
		/// </returns>
		public int GetBookComputedLevel()
		{
			var numberedPages = GetNumberedPages();

			int pageCount = 0;
			int maxWordsPerPage = 0;
			foreach (var pageElement in numberedPages)
			{
				++pageCount;
				int wordCountForThisPage = 0;

				IEnumerable<XmlElement> editables = GetEditablesFromPage(pageElement, Language1Code, includeImageDescriptions: false, includeTextOverPicture: true);
				foreach (var editable in editables)
				{
					wordCountForThisPage += GetWordCount(editable.InnerText);
				}

				maxWordsPerPage = Math.Max(maxWordsPerPage, wordCountForThisPage);
			}

			// This algorithm is to maintain consistentcy with African Storybook Project word count definitions
			// (Note: There are also guidelines about sentence count and paragraph count, which we could && in to here in the future).
			if (maxWordsPerPage <= 10)
				return 1;
			else if (maxWordsPerPage <= 25)
				return 2;
			else if (maxWordsPerPage <= 50)
				return 3;
			else
				return 4;
		}

		/// <summary>
		/// Returns the number of words in a piece of text
		/// </summary>
		internal static int GetWordCount(string text)
		{
			if (String.IsNullOrWhiteSpace(text))
				return 0;
			// FYI, GetWordsFromHtmlString() (which is a port from our JS code) returns an array containing the empty string
			// if the input to it is the empty string. So handle that...

			var words = GetWordsFromHtmlString(text);
			return words.Where(x => !String.IsNullOrEmpty(x)).Count();
		}

		private static readonly Regex kHtmlLinebreakRegex = new Regex("/<br><\\/br>|<br>|<br \\/>|<br\\/>|\r?\n/", RegexOptions.Compiled);
		/// <summary>
		/// Splits a piece of HTML text
		/// </summary>
		/// <param name="textHTML">The text to split</param>
		/// <param name="letters">Optional - Characters which Unicode defines as punctuation but which should be counted as letters instead</param>
		/// <returns>An array where each element represents a word</returns>
		private static string[] GetWordsFromHtmlString(string textHTML, string letters = null)
		{
			// This function is a port of the Javascript version in BloomDesktop's synphony_lib.js's getWordsFromHtmlString() function

			// Enhance: I guess it'd be ideal if we knew what the text's culture setting was, but I don't know how we can get that
			textHTML = textHTML.ToLower();

			// replace html break with space
			string s = kHtmlLinebreakRegex.Replace(textHTML, " ");

			var punct = "\\p{P}";

			if (!String.IsNullOrEmpty(letters))
			{
				// BL-1216 Use negative look-ahead to keep letters from being counted as punctuation
				// even if Unicode says something is a punctuation character when the user
				// has specified it as a letter (like single quote).
				punct = "(?![" + letters + "])" + punct;
			}
			/**************************************************************************
			 * Replace punctuation in a sentence with a space.
			 *
			 * Preserves punctuation marks within a word (ex. hyphen, or an apostrophe
			 * in a contraction)
			 **************************************************************************/
			var regex = new Regex(
				"(^" +
				punct +
				"+)" + // punctuation at the beginning of a string
				"|(" +
				punct +
				"+[\\s\\p{Z}\\p{C}]+" +
				punct +
				"+)" + // punctuation within a sentence, between 2 words (word" "word)
				"|([\\s\\p{Z}\\p{C}]+" +
				punct +
				"+)" + // punctuation within a sentence, before a word
				"|(" +
				punct +
				"+[\\s\\p{Z}\\p{C}]+)" + // punctuation within a sentence, after a word
					"|(" +
					punct +
					"+$)" // punctuation at the end of a string
			);
			s = regex.Replace(s, " ");

			// Split into words using Separator and SOME Control characters
			// Originally the code had p{C} (all Control characters), but this was too all-encompassing.
			const string whitespace = "\\p{Z}";
			const string controlChars = "\\p{Cc}"; // "real" Control characters
											// The following constants are Control(format) [p{Cf}] characters that should split words.
											// e.g. ZERO WIDTH SPACE is a Control(format) charactor
											// (See http://issues.bloomlibrary.org/youtrack/issue/BL-3933),
											// but so are ZERO WIDTH JOINER and NON JOINER (See https://issues.bloomlibrary.org/youtrack/issue/BL-7081).
											// See list at: https://www.compart.com/en/unicode/category/Cf
			const string zeroWidthSplitters = "\u200b"; // ZERO WIDTH SPACE
			const string ltrrtl = "\u200e\u200f"; // LEFT-TO-RIGHT MARK / RIGHT-TO-LEFT MARK
			const string directional = "\u202A-\u202E"; // more LTR/RTL/directional markers
			const string isolates = "\u2066-\u2069"; // directional "isolate" markers
											  // split on whitespace, Control(control) and some Control(format) characters
			regex = new Regex(
				"[" +
					whitespace +
					controlChars +
					zeroWidthSplitters +
					ltrrtl +
					directional +
					isolates +
					"]+"
			);
			return regex.Split(s.Trim());
		}

		private IEnumerable<XmlElement> GetNumberedPages() => _dom.SafeSelectNodes("//div[contains(concat(' ', @class, ' '),' numberedPage ')]").Cast<XmlElement>();

		private static string GetTranslationGroupsXpath(bool includeImageDescriptions)
		{
			string imageDescFilter = includeImageDescriptions ? "" : " and not(contains(@class,'bloom-imageDescription'))";
			string xPath = $"div[contains(@class,'marginBox')]//div[contains(@class,'bloom-translationGroup'){imageDescFilter}]";
			return xPath;
		}

		/// <summary>
		/// Gets the translation groups for the current page that are not within the image container
		/// </summary>
		/// <param name="pageElement">The page containing the bloom-editables</param>
		private static XmlNodeList GetTranslationGroupsFromPage(XmlElement pageElement, bool includeImageDescriptions)
		{
			return pageElement.SafeSelectNodes(GetTranslationGroupsXpath(includeImageDescriptions));
		}

		/// <summary>
		/// Gets the bloom-editables for the current page that match the language and are not within the image container
		/// </summary>
		/// <param name="pageElement">The page containing the bloom-editables</param>
		/// <param name="lang">Only bloom-editables matching this ISO language code will be returned</param>
		private static IEnumerable<XmlElement> GetEditablesFromPage(XmlElement pageElement, string lang, bool includeImageDescriptions = true, bool includeTextOverPicture = true)
		{
			string translationGroup = GetTranslationGroupsXpath(includeImageDescriptions);
			string langFilter = Bloom.Book.HtmlDom.IsLanguageValid(lang) ? $"[@lang='{lang}']" : "";

			string xPath = $"{translationGroup}//div[contains(@class,'bloom-editable')]{langFilter}";
			var editables = pageElement.SafeSelectNodes(xPath).Cast<XmlElement>();

			foreach (var editable in editables)
			{
				bool isOk = true;
				if (!includeTextOverPicture)
				{
					var textOverPictureMatch = GetClosestMatch(editable, (e) =>
					{
						return HtmlDom.HasClass(e, "bloom-textOverPicture");
					});

					isOk = textOverPictureMatch == null;
				}

				if (isOk)
					yield return editable;
			}
		}

		internal delegate bool ElementMatcher(XmlElement element);

		/// <summary>
		/// Find the closest ancestor (or self) that matches the condition
		/// </summary>
		/// <param name="startElement"></param>
		/// <param name="matcher">A function that returns true if the element matches</param>
		/// <returns></returns>
		internal static XmlElement GetClosestMatch(XmlElement startElement, ElementMatcher matcher)
		{
			XmlElement currentElement = startElement;
			while (currentElement != null)
			{
				if (matcher(currentElement))
				{
					return currentElement;
				}

				currentElement = currentElement.ParentNode as XmlElement;
			}

			return null;
		}

		/// <summary>
		/// Compute the perceptual hash of the given image file.  We need to handle black and white PNG
		/// files which carry the image data in only the alpha channel.  Other image files are trivial
		/// to handle by comparison with the CoenM.ImageSharp.ImageHash functions.
		/// </summary>
		public ulong ComputeImageHash(string path)
		{
			using (var image = (Image<Rgba32>)Image.Load(path))
			{
				// check whether we have R=G=B=0 (ie, black) for all pixels, presumably with A varying.
				var allBlack = true;
				for (int x = 0; allBlack && x < image.Width; ++x)
				{
					for (int y = 0; allBlack && y < image.Height; ++y)
					{
						var pixel = image[x, y];
						if (pixel.R != 0 || pixel.G != 0 || pixel.B != 0)
							allBlack = false;
					}
				}
				if (allBlack)
				{
					for (int x = 0; x < image.Width; ++x)
					{
						for (int y = 0; y < image.Height; ++y)
						{
							// If the pixels all end up the same because A never changes, we're no
							// worse off because the hash result will still be all zero bits.
							var pixel = image[x, y];
							pixel.R = pixel.A;
							pixel.G = pixel.A;
							pixel.B = pixel.A;
							image[x, y] = pixel;
						}
					}
				}
				var hashAlgorithm = new PerceptualHash();
				return hashAlgorithm.Hash(image);
			}
		}

		/// <summary>
		/// Finds the image to use when computing the perceptual hash for the book.
		/// </summary>
		/// <remarks>
		/// Precondition: Assumes that pages were written to the HTML in the order of their page number
		/// </remarks>
		public string GetBestPHashImageSource()
		{
			// Find the first picture on a content page
			// We use the numberedPage class to determine this now
			// You could also try data-page-number, but it's not guaranteed to use numbers like "1", "2", "3"... the numbers may be written in the language of the book (BL-8346)
			var firstContentImageContainerPath = "//div[contains(@class,'bloom-page')][contains(@class, 'numberedPage')]//div[contains(@class,'bloom-imageContainer')]";
			var firstContentImageElement = _dom.SelectSingleNode($"{firstContentImageContainerPath}/img");
			if (firstContentImageElement != null)
			{
				return firstContentImageElement.GetAttribute("src");
			}
			var fallbackFirstContentImage = _dom.SelectSingleNode(firstContentImageContainerPath);
			if (fallbackFirstContentImage != null)
			{
				return GetImageElementUrl(fallbackFirstContentImage)?.UrlEncoded;
			}
			// No content page images found.  Try the cover page
			var coverImageContainerPath = "//div[contains(@class,'bloom-page') and @data-xmatter-page='frontCover']//div[contains(@class,'bloom-imageContainer')]";
			var coverImg = _dom.SelectSingleNode($"{coverImageContainerPath}/img");
			if (coverImg != null)
			{
				return coverImg.GetAttribute("src");
			}
			var fallbackCoverImg = _dom.SelectSingleNode(coverImageContainerPath);
			if (fallbackCoverImg != null)
			{
				return GetImageElementUrl(fallbackCoverImg)?.UrlEncoded;
			}
			// Nothing on the cover page either. Give up.
			return null;
		}

		/// <summary>
		/// Gets the url for the image, either from an img element or any other element that has
		/// an inline style with background-image set.
		/// </summary>
		/// <remarks>
		/// This method is adapted (largely copied) from Bloom Desktop, so consider that if you
		/// need to modify this method.  The method is Bloom Desktop is not used because it would
		/// require adding a reference to Geckofx which is neither needed nor wanted here.
		/// </remarks>
		private UrlPathString GetImageElementUrl(XmlElement imgOrDivWithBackgroundImage)
		{
			if (imgOrDivWithBackgroundImage.Name.ToLower() == "img")
			{
				var src = imgOrDivWithBackgroundImage.GetAttribute("src");
				return UrlPathString.CreateFromUrlEncodedString(src);
			}
			var styleRule = imgOrDivWithBackgroundImage.GetAttribute("style") ?? "";
			var regex = new Regex("background-image\\s*:\\s*url\\((.*)\\)", RegexOptions.IgnoreCase);
			var match = regex.Match(styleRule);
			if (match.Groups.Count == 2)
			{
				return UrlPathString.CreateFromUrlEncodedString(match.Groups[1].Value.Trim(new[] {'\'', '"'}));
			}
			return null;
		}
	}
}
