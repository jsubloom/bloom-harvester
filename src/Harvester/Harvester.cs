using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web;
using Bloom.WebLibraryIntegration;
using BloomHarvester.IO;
using BloomHarvester.LogEntries;
using BloomHarvester.Logger;
using BloomHarvester.Parse;
using BloomHarvester.Parse.Model;
using BloomHarvester.WebLibraryIntegration;
using BloomTemp;
using SIL.IO;


namespace BloomHarvester
{
	// This class is responsible for coordinating the running of the application logic
	internal class Harvester : IDisposable
	{
		private const int kCreateArtifactsTimeoutSecs = 600;
		private const int kGetFontsTimeoutSecs = 20;
		private const bool kEnableLoggingSkippedBooks = false;


		protected HarvesterOptions _options;	// Keep a copy of the options passed in
		private EnvironmentSetting _parseDBEnvironment;
		private int _delayAfterEmptyRunSecs = 300;
		private DateTime _initTime;	// Used to provide a unique folder name for each Harvester instance
		private HashSet<string> _cumulativeFailedBookIdSet = new HashSet<string>();
		private HashSet<string> _missingFonts = new HashSet<string>();
		internal Version Version;
		private Random _rng = new Random();

		// BloomPub and other artifacts can be created when ePUBs fail.  This is normally due to the book
		// have Comic elements.  We don't want to fail harvesting just because Comic books can't have ePubs!
		// So we check for the ePUB file existing when Bloom finishes and reports success.  This information
		// is needed when setting Artifact suitability, not just when trying to upload the ePUB.
		private bool _ePubExists;

		// These vars handle the application being exited while a book is still InProgress
		private string _currentBookId = null;   // The ID of the current book for as long as that book has the "InProgress" state set on it. Should be set back to null/empty when the state is no longer "InProgress"
		private bool _currentBookFailedIndefinitely;	// flag that this book started with HarvestState = "FailedIndefinitely" and should stay there unless successful
		static ConsoleEventDelegate consoleExitHandler;
		private delegate bool ConsoleEventDelegate(int eventType);

		// Dependencies injected for unit testing
		protected readonly IParseClient _parseClient;
		protected readonly IS3Client _bloomS3Client;
		protected readonly IS3Client _s3UploadClient;  // Note that we upload books to a different bucket than we download them from, so we have a separate client.
		private readonly IBookTransfer _transfer;
		protected readonly IIssueReporter _issueReporter;
		protected readonly IBloomCliInvoker _bloomCli;
		private readonly IFontChecker _fontChecker;
		protected readonly IMonitorLogger _logger;
		private readonly IDiskSpaceManager _diskSpaceManager;
		protected readonly IFileIO _fileIO;

		internal bool IsDebug { get; set; }
		public string Identifier { get; set; }

		/// <summary>
		/// Primary constructor to be called externally
		/// </summary>
		/// <param name="options"></param>
		public Harvester(HarvesterOptions options)
			: this(options, GetConstructorArguments(options))
		{
		}

		private Harvester(HarvesterOptions options, (
			IIssueReporter issueReporter,
			EnvironmentSetting parseDBEnvironment,
			string identifier,
			IMonitorLogger logger,
			IParseClient parseClient,
			IS3Client s3DownloadClient,
			IS3Client s3uploadClient,
			IBookTransfer transfer,
			IBloomCliInvoker bloomCli,
			IDiskSpaceManager diskSpaceManager,
			IFontChecker fontChecker
			) args)
			:this(options, args.parseDBEnvironment, args.identifier, args.parseClient, args.s3DownloadClient, args.s3uploadClient, args.transfer,
				 args.issueReporter, args.logger, args.bloomCli, args.fontChecker, args.diskSpaceManager,
				 fileIO: new FileIO())
		{
		}

		/// <summary>
		/// This constructor allows the caller to pass in implementations of various components/dependencies
		/// In particular, this allows unit tests to pass in test doubles for outside components
		/// so that we don't actually update the database, issue-tracking system, etc.
		/// </summary>
		/// <param name="options">The options to control the Harvester settings</param>
		/// <param name="databaseEnvironment">The database environment</param>
		/// <param name="identifier">The identifier (name) of this instance</param>
		/// <param name="parseClient">The database client</param>
		/// <param name="s3DownloadClient">The client that responsible for downloading books</param>
		/// <param name="s3UploadClient">The client responsible for uploading harvested artifacts</param>
		/// <param name="transfer">The transfer client which assists in downloading books</param>
		/// <param name="issueReporter">The issue tracking system</param>
		/// <param name="logger">The component that logs the output to console, Azure monitor, etc</param>
		/// <param name="bloomCliInvoker">Handles invoking the Bloom command line</param>
		/// <param name="fileIO">Handles File input/output</param>
		internal Harvester(
			HarvesterOptions options,
			EnvironmentSetting databaseEnvironment,
			string identifier,
			IParseClient parseClient,
			IS3Client s3DownloadClient,
			IS3Client s3UploadClient,
			IBookTransfer transfer,
			IIssueReporter issueReporter,
			IMonitorLogger logger,
			IBloomCliInvoker bloomCliInvoker,
			IFontChecker fontChecker,
			IDiskSpaceManager diskSpaceManager,
			IFileIO fileIO)
		{
			// Just copying from parameters
			_options = options;
			_parseDBEnvironment = databaseEnvironment;
			this.Identifier = identifier;
			// Continue copying dependencies injected for unit testing purposes			
			_parseClient = parseClient;
			_bloomS3Client = s3DownloadClient;
			_s3UploadClient = s3UploadClient;
			_transfer = transfer;
			_issueReporter = issueReporter;
			_bloomCli = bloomCliInvoker;
			_fontChecker = fontChecker;
			_logger = logger;
			_diskSpaceManager = diskSpaceManager;
			_fileIO = fileIO;

			// Additional constructor setup
			_initTime = DateTime.Now;

			_issueReporter.Disabled = options.SuppressErrors;

			var assemblyVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version ?? new Version(0, 0);
			this.Version = new Version(assemblyVersion.Major, assemblyVersion.Minor);	// Only consider the major and minor version

			if (options.LoopWaitSeconds >= 0)
			{
				_delayAfterEmptyRunSecs = options.LoopWaitSeconds;
			}

			AlertManager.Instance.Logger = _logger;

			// Setup a handler that is called when the console is closed
			consoleExitHandler = new ConsoleEventDelegate(ConsoleEventCallback);
			SetConsoleCtrlHandler(consoleExitHandler, add: true);
		}
		/// <summary>
		/// Do a lot of complex initailization
		/// A lot of this code is iterative statements that depends on previous statements...
		/// writing it this way allows us to avoid numerous layers of constructor chaining that slowly add parameters one at a time
		/// </summary>
		/// <param name="options"></param>
		/// <returns>A tuple containing all the construction-related variables</returns>
		private static (
			IIssueReporter issueReporter,
			EnvironmentSetting parseDBEnvironment,
			string identifier,
			IMonitorLogger logger,
			IParseClient parseClient,
			IS3Client s3DownloadClient,
			IS3Client s3uploadClient,
			IBookTransfer transfer,
			IBloomCliInvoker bloomCli,
			IDiskSpaceManager diskSpaceManager,
			IFontChecker fontChecker
		) GetConstructorArguments(HarvesterOptions options)
		{
			// Safer to get this issueReporter stuff out of the way as first, in case any construction code generates a YouTrack issue.
			var parseDBEnvironment = EnvironmentUtils.GetEnvOrFallback(options.ParseDBEnvironment, options.Environment);
			var issueReporter = YouTrackIssueConnector.GetInstance(parseDBEnvironment);
			issueReporter.Disabled = options.SuppressErrors;

			var logEnvironment = EnvironmentUtils.GetEnvOrFallback(options.LogEnvironment, options.Environment);

			string identifier = $"{Environment.MachineName}-{parseDBEnvironment.ToString()}";
			IMonitorLogger logger = options.SuppressLogs ? new ConsoleLogger() : (IMonitorLogger)new AzureMonitorLogger(logEnvironment, identifier);

			var parseClient = new ParseClient(parseDBEnvironment, logger);
			(string downloadBucketName, string uploadBucketName) = GetS3BucketNames(parseDBEnvironment);
			var s3DownloadClient = new HarvesterS3Client(downloadBucketName, parseDBEnvironment, true);
			var s3UploadClient = new HarvesterS3Client(uploadBucketName, parseDBEnvironment, false);

			var transfer = new HarvesterBookTransfer(parseClient,
				bloomS3Client: s3DownloadClient,
				htmlThumbnailer: null);

			var bloomCli = new BloomCliInvoker(logger);
			var fontChecker = new FontChecker(kGetFontsTimeoutSecs, bloomCli, logger);

			var driveInfo = GetHarvesterDrive();
			var diskSpaceManager = new DiskSpaceManager(driveInfo, logger, issueReporter);

			return (issueReporter, parseDBEnvironment, identifier, logger, parseClient, s3DownloadClient, s3UploadClient, transfer, bloomCli, diskSpaceManager, fontChecker);
		}

		/// <summary>
		/// Based on the Parse environment, determines the Amazon S3 bucket names to download and upload from.
		/// </summary>
		/// <returns>A tuple of 2 strings. The first string is the bucket name from which to download books. The 2nd is the bucket name to upload harvested artifacts</returns>
		protected static (string downloadBucketName, string uploadBucketName) GetS3BucketNames(EnvironmentSetting parseDBEnvironment)
		{
			string downloadBucketName, uploadBucketName;

			switch (parseDBEnvironment)
			{
				case EnvironmentSetting.Prod:
					downloadBucketName = BloomS3Client.ProductionBucketName;
					uploadBucketName = HarvesterS3Client.HarvesterProductionBucketName;
					break;
				case EnvironmentSetting.Test:
					downloadBucketName = BloomS3Client.UnitTestBucketName;
					uploadBucketName = HarvesterS3Client.HarvesterUnitTestBucketName;
					break;
				case EnvironmentSetting.Dev:
				case EnvironmentSetting.Local:
				default:
					downloadBucketName = BloomS3Client.SandboxBucketName;
					uploadBucketName = HarvesterS3Client.HarvesterSandboxBucketName;
					break;
			}

			return (downloadBucketName, uploadBucketName);
		}

		public void Dispose()
		{
			_logger.Dispose();
		}

		private string _uid;

		/// <summary>
		/// Uniquely identifies a Harvester with a Base64 encoded Guid.  We try to minimize
		/// the length of this identifier to minimize its impact on the total path length.
		/// This enables us to tell apart two Harvesters running on the same machine.
		/// </summary>
		public string GetUniqueIdentifier()
		{
			if (String.IsNullOrEmpty(_uid))
			{
				var guid = Guid.NewGuid();
				// https://www.stevejgordon.co.uk/using-high-performance-dotnetcore-csharp-techniques-to-base64-encode-a-guid
				// is an interesting article on optimizing this, but we don't do it enough to worry about optimizing.
				_uid = Convert.ToBase64String(guid.ToByteArray()).Replace("/", "-").Replace("+", "_").Replace("=", "");
			}
			return _uid;
		}

		/// <summary>
		/// Handles control signals received by the process
		/// https://docs.microsoft.com/en-us/windows/console/handlerroutine
		/// </summary>
		/// <param name="eventType"></param>
		/// <returns>True if the event was handled by this function. False otherwise (i.e, let any subsequent handlers take a stab at it)</returns>
		bool ConsoleEventCallback(int eventType)
		{
			// See https://stackoverflow.com/a/4647168 for reference

			if (eventType == 2) // CTRL_CLOSE_EVENT - The console is being closed
			{
				// The console is being closed but it looks like we were in the middle of processing some book (which may or may not succeed if allowed to finish).
				// Before closing, try to update the state in the database so that it's not stuck in "InProgress"
				if (!String.IsNullOrEmpty(_currentBookId))
				{
					var updateOp = BookModel.GetNewBookUpdateOperation();

					if (_currentBookFailedIndefinitely)
						updateOp.UpdateFieldWithString(BookModel.kHarvestStateField, HarvestState.FailedIndefinitely.ToString());
					else
						updateOp.UpdateFieldWithString(BookModel.kHarvestStateField, HarvestState.Aborted.ToString());
					_parseClient.UpdateObject(BookModel.GetStaticParseClassName(), _currentBookId, updateOp.ToJson());
					return true;
				}
			}

			return false;
		}

		[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

		public static void RunHarvest(HarvesterOptions options)
		{
			Console.Out.WriteLine($"Command Line Options: \n" + options.GetPrettyPrint());
			Console.Out.WriteLine();

			using (Harvester harvester = new Harvester(options))
			{
				harvester.Harvest(maxBooksToProcess: options.Count, queryWhereJson: options.QueryWhere);
			}
		}

		/// <summary>
		/// Process all rows in the books table
		/// Public interface should use RunHarvest() function instead. (So that we can guarantee that the class instance is properly disposed).
		/// </summary>
		/// 
		/// <param name="maxBooksToProcess"></param>
		private void Harvest(int maxBooksToProcess = -1, string queryWhereJson = "")
		{
			_logger.TrackEvent($"Harvest{_options.Mode} Start");

			var additionalWhereFilters = GetQueryWhereOptimizations();
			string combinedWhereJson = Harvester.InsertQueryWhereOptimizations(queryWhereJson, additionalWhereFilters);
			Console.Out.WriteLine("combinedWhereJson: " + combinedWhereJson);

			do
			{
				try
				{
					Console.Out.WriteLine();
					
					var methodStopwatch = new Stopwatch();
					methodStopwatch.Start();

					IEnumerable<BookModel> bookList = _parseClient.GetBooks(out bool didExitPrematurely, combinedWhereJson);

					if (didExitPrematurely && !_options.Loop)
					{
						// If GetBooks exited prematurely (i.e., partial results), AND LOOP IS NOT SET,
						// then we don't want to risk the user getting confused with only some of the intended books getting processed.
						// So we just abort it. All or nothing.
						//
						// On the other hand, if LOOP was set, we'll just do a best-effort on this iteration, and eventually on some future iteration hopefully it should work.
						_logger.LogError("GetBooks() encountered an error and did not return all results. Aborting.");
						break;
					}

					if (bookList == null)
					{
						continue;
					}

					// Prioritize New books first.
					// It would be nice to push this into the Parse query, but the "order" parameter seems to only allow sorting by a field. Can't find any info about sorting by more complicated expressions.
					//
					// Current assumptions are that the program is normally run in Default Mode with count=1 and loop flag set.
					// Also that it does not take long to get the results of the Parse query or sort them.
					// In that scenario I think it makes sense to just do a single Parse query
					// But if assumptions change (like if usual call parameters change or it becomes expensive to retrieve all the columns from Parse), it could become worthwhile to split into multiple Parse queries.
					bookList = bookList.OrderByDescending(x => x.HarvestState == Parse.Model.HarvestState.New.ToString())   // TRUE (1) cases first, FALSE (0) cases second. i.e. State=New first, then everything else
						.ThenByDescending(x => x.HarvestState == Parse.Model.HarvestState.Updated.ToString())	// State=Updated first, then everything else.
						// Enhance: Could also add another level to check whether the book's updatedTime < start time of the program. It would help in situations like where mode=all, count=1, and loop=true to ensure that every book gets processed once first before probably re-doing some books. But this is not normally needed when mode=default
						.ThenBy(x => _rng.Next());   // Randomize within each section.

					int numBooksProcessed = 0;

					var skippedBooks = new List<BookModel>();
					var failedBooks = new List<BookModel>();	// Only the list from the current iteration, not the total cumulative list

					foreach (var bookModel in bookList)
					{
						try
						{
							var book = new Book(bookModel, _logger, _fileIO);

							// Decide if we should process it.
							bool shouldBeProcessed = ShouldProcessBook(bookModel, out string reason);
							if (shouldBeProcessed || kEnableLoggingSkippedBooks)
							{
								_logger.LogInfo($"{bookModel.ObjectId} - {reason}");
							}

							if (!shouldBeProcessed)
							{
								skippedBooks.Add(bookModel);

								if (bookModel.HarvestState == Parse.Model.HarvestState.Done.ToString())
								{
									// Something else has marked this book as no longer failed
									_cumulativeFailedBookIdSet.Remove(bookModel.ObjectId);
								}

								continue;
							}
							
							bool isSuccessful = ProcessOneBook(book);
							if (isSuccessful)
							{
								// We know this book is no longer failed
								_cumulativeFailedBookIdSet.Remove(bookModel.ObjectId);
							}
							else
							{
								failedBooks.Add(bookModel);
							}
							++numBooksProcessed;

							if (maxBooksToProcess > 0 && numBooksProcessed >= maxBooksToProcess)
							{
								break;
							}
						}
						catch (Exception e)
						{
							_issueReporter.ReportException(e, $"Unhandled exception thrown while running Harvest() function on a book.", bookModel, exitImmediately: false);
						}
					}

					methodStopwatch.Stop();
					Console.Out.WriteLine($"Harvest{_options.Mode} took {(methodStopwatch.ElapsedMilliseconds / 1000.0):0.0} seconds.");

					if (kEnableLoggingSkippedBooks && skippedBooks.Any())
					{
						// There is a flag enabled for logging these because it can be useful to see in the development phase, but not likely to be useful when it's running normally.
						string warningMessage = "Skipped Book ObjectIds:\n\t" + String.Join("\t", skippedBooks.Select(x => x.ObjectId));
						_logger.LogVerbose(warningMessage);
					}

					if (_cumulativeFailedBookIdSet?.Any() == true)
					{
						var sample = _cumulativeFailedBookIdSet.Take(10);
						string errorMessage = $"Books with outstanding errors from previous iterations (sample of {sample.Count()}):\n\t" + String.Join("\n\t", sample.Select(id => $"ObjectId: {id}"));
						_logger.LogInfo(errorMessage);
					}

					if (failedBooks.Any())
					{
						string errorMessage = "Books with errors (this iteration only):\n\t" + String.Join("\n\t", failedBooks.Select(x => $"ObjectId: {x.ObjectId}.  URL: {x.BaseUrl}"));
						_logger.LogError(errorMessage);

						_cumulativeFailedBookIdSet?.UnionWith(failedBooks.Select(x => x.ObjectId));
					}


					int numBooksFailed = failedBooks.Count;
					int numBooksSkipped = skippedBooks.Count;
					int numBooksTotal = numBooksSkipped + numBooksProcessed;
					int numBooksSuccess = numBooksProcessed - numBooksFailed;
					_logger.LogInfo($"Success={numBooksSuccess}, Failed={numBooksFailed}, Skipped={numBooksSkipped}, Total={numBooksTotal}.");

					if (numBooksFailed > 0)
					{
						double percentFailed = ((double)numBooksFailed) / numBooksTotal * 100;
						if (percentFailed > 0 && percentFailed < 0.1)
						{
							percentFailed = 0.1;    // Don't round non-zero numbers down to 0. It might make the log misleading.
						}
						_logger.LogError($"Failures ({percentFailed:0.0}% failed)");
					}

					if (_options.Loop)
					{
						_logger.TrackEvent($"Harvest{_options.Mode} Loop Iteration completed.");

						if (numBooksProcessed == 0)
						{
							var estimatedResumeTime = DateTime.Now.AddSeconds(_delayAfterEmptyRunSecs);
							Console.Out.WriteLine($"Waiting till: {estimatedResumeTime.ToString("h:mm:ss tt")}...");
							Thread.Sleep(_delayAfterEmptyRunSecs * 1000);
						}
						else
						{
#if DEBUG
							Thread.Sleep(5);   // Just for debugging purposes to see what's going on
#endif
						}
					}
				}
				catch (Exception e)
				{
					try
					{
						_issueReporter.ReportException(e, $"Unhandled exception thrown while running Harvest() function.", null, exitImmediately: false);
					}
					catch (Exception)
					{
						// There was an error in reporting the error...
						// That's unfortunate, but we don't want to propagate another exception higher up. Because that would suspend our loop
						Console.Error.WriteLine("Exception thrown while attempting to report exception to YouTrack");
					}
				}

			} while (_options.Loop);

			_logger.TrackEvent($"Harvest{_options.Mode} End");
		}

		internal List<string> GetQueryWhereOptimizations()
		{
			// These filters should keep every book we need to process, but it's ok to include some books we don't need to process.
			// We will still call ShouldProcessBook later to do more checking.
			var whereOptimizationConditions = new List<string>();
			// If (!ForceAll) then add (inCirculation == true || inCirculation not set)
			var inCirculation = "\"$or\":[{\"inCirculation\":true},{\"inCirculation\":{\"$exists\":false}}]";
			// version X.Y => (HarvesterMajorVersion < X) || (HarvesterMajorVersion == X && HarvesterMinorVersion < Y) || (HarvesterMajorVersion doesn't exist)
			var previousVersion = GetVersionFilterString(includeCurrentVersion: false);
			// (the HarvesterMinorVersion <= Y for including the current version)
			var previousOrCurrentVersion = GetVersionFilterString(includeCurrentVersion: true);
			switch (_options.Mode)
			{
				case HarvestMode.ForceAll:
					// no filtering added: we want EVERYTHING!
					break;
				case HarvestMode.All:
					// all books in circulation
					whereOptimizationConditions.Add(inCirculation);
					break;
				case HarvestMode.NewOrUpdatedOnly:
					// all books in circulation AND with the state in [New, Updated, Unknown]
					whereOptimizationConditions.Add("\"harvestState\" : { \"$in\": [\"New\", \"Updated\", \"Unknown\"]}");
					whereOptimizationConditions.Add(inCirculation);
					break;
				case HarvestMode.RetryFailuresOnly:
					// all books in circulation AND harvested by current or previous version of harvester AND state is Failed
					whereOptimizationConditions.Add("\"harvestState\": \"Failed\"");
					whereOptimizationConditions.Add(previousOrCurrentVersion);
					whereOptimizationConditions.Add(inCirculation);
					break;
				case HarvestMode.Default:
				default:
					// all books in circulation AND EITHER harvested by previous version of harvester OR state not in [Done, Failed]
					whereOptimizationConditions.Add($"\"$or\":[{{{previousVersion}}},{{\"harvestState\":{{\"$nin\":[\"Done\",\"Failed\"]}}}}]");
					whereOptimizationConditions.Add(inCirculation);
					break;
			}
			return whereOptimizationConditions;
		}

		private string GetVersionFilterString(bool includeCurrentVersion)
		{
			var versionComparison = includeCurrentVersion ? "$lte" : "$lt";
			return "\"$or\":[" +
	                     $"{{\"harvesterMajorVersion\":{{\"$lt\":{this.Version.Major}}}}}," +
	                     $"{{\"harvesterMajorVersion\":{this.Version.Major},\"harvesterMinorVersion\":{{\"{versionComparison}\":{this.Version.Minor}}}}}," +
	                     "{\"harvesterMajorVersion\":{\"$exists\":false}}" +
	                     "]";
		}

		internal static string InsertQueryWhereOptimizations(string userInputQueryWhere, List<string> whereConditions)
		{
			if (userInputQueryWhere == null)
				userInputQueryWhere = "";	// simplify processing to not need multiple null checks
			// if no additional conditions apply, just return the user input (which may be empty)
			if (whereConditions.Count == 0)
				return userInputQueryWhere;
			var userInput = userInputQueryWhere.Trim();
			// To simplify processing below, strip the surrounding braces from the user's input condition.
			if (userInput.StartsWith("{") && userInput.EndsWith("}"))
			{
				userInput = userInput.Substring(1, userInput.Length - 2);
				userInput = userInput.Trim();
			}
			// If the user input is not empty, add it as the last (possibly only) condition.
			if (!String.IsNullOrEmpty(userInput))
				whereConditions.Add(userInput);
			var bldr = new StringBuilder();
			bldr.Append("{");
			if (whereConditions.Count == 1)
			{
				bldr.Append(whereConditions[0]);
			}
			else
			{
				bldr.Append("\"$and\":[{");
				bldr.Append(String.Join("},{", whereConditions));
				bldr.Append("}]");
			}
			bldr.Append("}");
			return bldr.ToString();
		}
				
		internal bool ProcessOneBook(Book book)
		{
			bool isSuccessful = true;
			string collectionBookDir = null;
			// save original values of these options in case they're needed.
			var skipUploadEPub = _options.SkipUploadEPub;
			var skipUploadBloomDigitalArtifacts = _options.SkipUploadBloomDigitalArtifacts;
			try
			{
				string message = $"Processing: {book.Model.BaseUrl}";
				_logger.LogVerbose(message);

				_logger.TrackEvent("ProcessOneBook Start"); // After we check ShouldProcessBook

				// Parse DB initial updates
				// We want to write that it is InProgress as soon as possible, but we also want a copy of the original state
				var originalBookModel  = (BookModel)book.Model.Clone();
				_currentBookFailedIndefinitely = originalBookModel.HarvestState?.ToLowerInvariant() == HarvestState.FailedIndefinitely.ToString().ToLowerInvariant();
				book.Model.HarvestState = Parse.Model.HarvestState.InProgress.ToString();
				book.Model.HarvesterId = this.Identifier;
				book.Model.HarvesterMajorVersion = Version.Major;
				book.Model.HarvesterMinorVersion = Version.Minor;
				book.Model.HarvestStartedAt = new Parse.Model.ParseDate(DateTime.UtcNow);

				if (!_options.ReadOnly)
				{
					_currentBookId = book.Model.ObjectId;					
				}
				book.Model.FlushUpdateToDatabase(_parseClient, _options.ReadOnly);

				// Download the book
				string decodedUrl = HttpUtility.UrlDecode(book.Model.BaseUrl);
				collectionBookDir = DownloadBookAndCopyToCollectionFolder(book, decodedUrl, originalBookModel);

				// Process the book
				List<LogEntry> harvestLogEntries = CheckForMissingFontErrors(collectionBookDir, book);
				bool anyFontsMissing = harvestLogEntries.Any();
				isSuccessful &= !anyFontsMissing;
				if (anyFontsMissing)
				{
					_options.SkipUploadBloomDigitalArtifacts = true;
					_options.SkipUploadEPub = true;
				}

				// More processing
				var warnings = book.FindBookWarnings();
				harvestLogEntries.AddRange(warnings);

				if (!_options.ReadOnly)
				{
					var analyzer = GetAnalyzer(collectionBookDir);
					var collectionFilePath = analyzer.WriteBloomCollection(collectionBookDir);
					book.Analyzer = analyzer;

                    // This must run before CreateArtifacts because Bloom can change the actual folder name for
                    // collectionBookDir if it is an artifact of multiple copies of the same title on the uploader's
                    // computer.  See https://issues.bloomlibrary.org/youtrack/issue/BH-5551.
					if (!_options.SkipUpdatePerceptualHash)
						isSuccessful &= UpdatePerceptualHash(book, analyzer, collectionBookDir, harvestLogEntries);

					isSuccessful &= CreateArtifacts(decodedUrl, collectionBookDir, collectionFilePath, book,
						harvestLogEntries);
					// If not successful, update artifact suitability to say all false. (BL-8413)
					UpdateSuitabilityOfArtifacts(book, analyzer, isSuccessful, anyFontsMissing, harvestLogEntries);

					book.SetTags();
				}

				// Finalize the state
				book.Model.HarvestLogEntries = harvestLogEntries.Select(x => x.ToString()).ToList();
				if (isSuccessful)
				{
					book.Model.HarvestState = Parse.Model.HarvestState.Done.ToString();
				}
				else
				{
					SetFailedState(book);
				}

				// Write the updates
				_currentBookId = null;
				book.Model.FlushUpdateToDatabase(_parseClient, _options.ReadOnly);
				_logger.TrackEvent("ProcessOneBook End - " + (isSuccessful ? "Success" : "Error"));
			}
			catch (Exception e)
			{
				isSuccessful = false;
				string bookId = book.Model?.ObjectId ?? "null";
				string bookUrl = book.Model?.BaseUrl ?? "null";
				string errorMessage = $"Unhandled exception \"{e.Message}\" thrown.";
				// On rare occasions, someone may delete a book just as we're processing it.  If that happens, don't bother
				// reporting a bug to YouTrack. (BH-5480 & BL-8388)
				var skipBugReport = bookId != "null" && bookUrl != "null" &&
				                    ((e is ParseException && errorMessage.Contains("Response.Code: NotFound")) ||
				                     (e is DirectoryNotFoundException && errorMessage.Contains("tried to download")));
				if (skipBugReport)
				{
					_logger.TrackEvent("Possible book deletion");
					var msgFormat =
						$"ProcessOneBook - Exception caught, book {bookId} ({bookUrl}) may have been deleted.{Environment.NewLine}{{0}}";
					_logger.LogWarn(msgFormat, e.Message);
					// If the book has been deleted, the parse table row will also have been deleted.
					// (In fact, that's what a ParseException with NotFound is telling us.)
					return isSuccessful;
				}
				_issueReporter.ReportException(e, errorMessage, book.Model, exitImmediately: false);

				// Attempt to write to Parse that processing failed
				if (!String.IsNullOrEmpty(book.Model?.ObjectId) && !skipBugReport)
				{
					try
					{
						SetFailedState(book);
						book.Model.HarvesterId = this.Identifier;
						if (book.Model.HarvestLogEntries == null)
						{
							book.Model.HarvestLogEntries = new List<string>();
						}
						var logEntries = new List<LogEntry>();
						logEntries.Add(new LogEntry(LogLevel.Error, LogType.ProcessBookError, errorMessage));

						// These options need to be restored before calling UpdateSuitabilityOfArtifacts
						_options.SkipUploadBloomDigitalArtifacts = skipUploadBloomDigitalArtifacts;
						_options.SkipUploadEPub = skipUploadEPub;
						UpdateSuitabilityOfArtifacts(book, null, false, false, logEntries);
						book.Model.HarvestLogEntries.AddRange(logEntries.Select(x => x.ToString()).ToList());
						book.Model.FlushUpdateToDatabase(_parseClient);
					}
					catch (Exception)
					{
						// If it fails, just let it be and report the first exception rather than the nested exception.
					}
				}
			}
			finally
			{
				// clean up after ourselves: we only need to preserve the copy in the download cache folder.
				if (Directory.Exists(collectionBookDir))
					Directory.Delete(collectionBookDir, true);

				// Make sure to report these so that a missing font on the 1st book doesn't change the options for future books.
				_options.SkipUploadBloomDigitalArtifacts = skipUploadBloomDigitalArtifacts;
				_options.SkipUploadEPub = skipUploadEPub;
			}

			return isSuccessful;
		}

		/// <summary>
		/// Download the book if necessary, ensuring that it ends up in a unique folder in the cache
		/// folder.  Then copy the book folder to the collection folder under the book's title.
		/// </summary>
		/// <returns>file path of the book's folder in the collection folder</returns>
		private string DownloadBookAndCopyToCollectionFolder(Book book, string decodedUrl, BookModel originalBookModel)
		{
			var urlComponents = new S3UrlComponents(decodedUrl);

			// The download/cache folder must use unique names for books to avoid collisions.  The book's
			// objectId seems like as good as a choice as any.  Titles certainly aren't unique!
			string downloadBookDir = Path.Combine(this.GetBookCacheFolder(), book.Model.ObjectId);
			// Note: Make sure you use the title as it appears on the filesystem.
			// It may differ from the title in the Parse DB if the title contains punctuation which are not valid in file paths.
			var collectionBookDir = Path.Combine(this.GetBookCollectionPath(), urlComponents.BookTitle);
			bool canUseExisting = TryUseExistingBookDownload(originalBookModel, downloadBookDir);

			if (canUseExisting)
			{
				_logger.TrackEvent("Using Cached Book");
			}
			else
			{
				_logger.TrackEvent("Download Book");

				// Check on how we're doing on disk space
				_diskSpaceManager?.CleanupIfNeeded();

				// FYI, there's no need to delete this book folder first.
				// _transfer.HandleDownloadWithoutProgress() removes outdated content for us.
				string urlWithoutTitle = RemoveBookTitleFromBaseUrl(decodedUrl);
				string downloadRootDir = GetBookCacheFolder();
				// HandleDownloadWithoutProgress has a nested subcall to BloomS3Client.cs::AvoidThisFile() which looks at HarvesterMode
				Bloom.Program.RunningHarvesterMode = true;
				var downloadedDir = _transfer.HandleDownloadWithoutProgress(urlWithoutTitle, downloadRootDir);
				// The download process appears to inherently use the book's title, so we need
				// to rename the folder to what we want for caching purposes.
				if (downloadedDir != downloadBookDir)
				{
					if (Directory.Exists(downloadBookDir))
						Directory.Delete(downloadBookDir, true);
					Directory.Move(downloadedDir, downloadBookDir);
				}
			}
			// Copy the book's folder from the cache location to the collection folder.
			Directory.CreateDirectory(GetBookCollectionPath());
			if (Directory.Exists(collectionBookDir))
				Directory.Delete(collectionBookDir, true); // best to be safe...
			DirectoryHelper.Copy(downloadBookDir, collectionBookDir, true);
			return collectionBookDir;
		}

		private bool UpdatePerceptualHash(Book book, IBookAnalyzer analyzer, string downloadBookDir, IList<LogEntry> logEntries)
		{
			var isSuccessful = true;
			var startTime = DateTime.Now;
			string src = null;
			try
			{
				src = analyzer.GetBestPHashImageSource();
				if (!String.IsNullOrEmpty(src) && src.ToLowerInvariant() != "placeholder.png")
				{
					string decodedSrc = HttpUtility.UrlDecode(src);
					var path = Path.Combine(downloadBookDir, decodedSrc);
					ulong imageHash = analyzer.ComputeImageHash(path);
					book.Model.PHashOfFirstContentImage = $"{imageHash:X16}";
				}
				else
				{
					book.Model.PHashOfFirstContentImage = null;
				}
			}
			catch (Exception e)
			{
				book.Model.PHashOfFirstContentImage = null;
				isSuccessful = false;
				logEntries.Add(new LogEntry(LogLevel.Error, LogType.PHashError, "Exception thrown. " + e.Message));
				_logger.LogWarn("Caught exception computing phash for {0}: {1}", src, e);
				_logger.LogWarn(e.StackTrace);
				_issueReporter.ReportException(e, $"Caught exception computing phash for {src}", book.Model, exitImmediately: false);
			}
			var endTime = DateTime.Now;
			_logger.LogVerbose("Computing PHash=\"{0}\" for {1}/{2} took {3}", book.Model.PHashOfFirstContentImage, book.Model.ObjectId, src, endTime - startTime);
			return isSuccessful;
		}

		private void SetFailedState(Book book)
		{
			if (_currentBookFailedIndefinitely)
				book.Model.HarvestState = HarvestState.FailedIndefinitely.ToString();
			else
				book.Model.HarvestState = HarvestState.Failed.ToString();
		}

		/// <summary>
		/// Determines whether or not we can/should use an existing book download
		/// </summary>
		/// <param name="originalBookModel">The bookModel at the very beginning of processing, without any updates we've made.</param>
		/// <param name="pathToExistingBook">The path of the book to check</param>
		/// <returns>True if it's ok to use</returns>
		private bool TryUseExistingBookDownload(BookModel originalBookModel, string pathToCheck)
		{
			if (_options.ForceDownload)
				return false;

			if (_options.SkipDownload)
				return Directory.Exists(pathToCheck);

			ParseDate lastHarvestedDate = originalBookModel.HarvestStartedAt;
			if (lastHarvestedDate == null)
				// Not harvested previously. Need to download it for the first time.
				return false;

			ParseDate lastUploadedDate = originalBookModel.LastUploaded;

			// For a long time, lastUploadedDate was not set. This will show up as (undefined) in the ParseDB,
			// which shows up as null for the ParseDate here in C# land.
			// lastUploadedDate didn't start getting set in Production until partway through 4-6-2020 (UTC time)
			// (although it started appearing in March on Dev).
			// We treat null lastUploadDate as if it were uploaded at the end of 4-6-2020,
			// since we cannot rule out the possibility that it was uploaded as late as this time.
			if (lastUploadedDate == null)
			{
				lastUploadedDate = new ParseDate(new DateTime(2020, 04, 06, 23, 59, 59, DateTimeKind.Utc));
			}

			if (lastHarvestedDate.UtcTime <= lastUploadedDate.UtcTime)
			{
				// It's been uploaded (or potentially uploaded) since the last time we harvested.
				// Must not use the existing one... we need to re-download it
				return false;
			}

			// It's feasible to re-use the existing directory,
			// but only if it actually exists
			return Directory.Exists(pathToCheck);
		}

		internal virtual IBookAnalyzer GetAnalyzer(string collectionBookDir)
		{
			return BookAnalyzer.FromFolder(collectionBookDir);
		}

		/// <summary>
		/// If any fonts are missing, isSuccessful will be false and both epubs and bloompubs will be marked as false for showing.
		/// (The missing font prevented them from being created in the form the author desired.)  Otherwise, epubs and bloompubs
		/// are marked true or false for showing only if we tried to make them (ie, didn't skip them).  If we skipped one or both
		/// of them, the previous evaluation is left alone for whatever was skipped.
		/// </summary>
		private void UpdateSuitabilityOfArtifacts(Book book, IBookAnalyzer analyzer, bool isSuccessful, bool anyFontsMissing,
			List<LogEntry> harvestLogEntries)
		{
			if (anyFontsMissing)
				harvestLogEntries.Add(new LogEntry(LogLevel.Info, LogType.ArtifactSuitability, "No ePUB/BloomPub because of missing font(s)"));
			else if (!isSuccessful)
				harvestLogEntries.Add(new LogEntry(LogLevel.Info, LogType.ArtifactSuitability, $"No ePUB/BloomPub because CreateArtifacts failed."));

			if (!_options.SkipUploadEPub || anyFontsMissing)
			{
				book.SetHarvesterEvaluation("epub", isSuccessful && _ePubExists && analyzer.IsEpubSuitable(harvestLogEntries));
			}

			if (!_options.SkipUploadBloomDigitalArtifacts || anyFontsMissing)
			{
				var isBloomReaderGood = isSuccessful && analyzer.IsBloomReaderSuitable(harvestLogEntries);
				book.SetHarvesterEvaluation("bloomReader", isBloomReaderGood);
				book.SetHarvesterEvaluation("readOnline", isBloomReaderGood);
			}

			// harvester never makes pdfs at the moment.
		}

		private bool ShouldProcessBook(BookModel book, out string reason)
		{
			return ShouldProcessBook(book, _options.Mode, this.Version, out reason);
		}

		/// <summary>
		/// Determines whether or not a book should be processed by the current harvester
		/// </summary>
		/// <param name="book"></param>
		/// <param name="reason">If the method returns true, then reason must be assigned with an explanation of why the book was selected for processing</param>
		/// <returns>Returns true if the book should be processed</returns>
		public static bool ShouldProcessBook(BookModel book, HarvestMode harvestMode, Version currentVersion, out string reason)
		{
			Debug.Assert(book != null, "ShouldProcessBook(): Book was null");

			if (!Enum.TryParse(book.HarvestState, out HarvestState state))
			{
				throw new Exception($"Invalid book.HarvestState \"{book.HarvestState}\" for book.ObjectId=\"{book.ObjectId}\"");
			}

			// If we're forcing all books, we're forcing all books.  Go for it.
			if (harvestMode == HarvestMode.ForceAll)
			{
				reason = "PROCESS: Mode = HarvestForceAll";
				return true;
			}

			// Skip books that are marked as "failed indefinitely".  They will get processed if they get re-uploaded
			// (which changes the state to Updated).
			if (state == HarvestState.FailedIndefinitely)
			{
				reason = "SKIP: Marked as failed indefinitely";
				return false;
			}

			// Skip books that are explicitly marked as out of circulation.
			// Note: Beware, IsInCirculation can also be null, and we DO want to process books where isInCirculation==null
			if (book.IsInCirculation == false)
			{
				reason = "SKIP: Not in circulation";
				return false;
			}

			bool isNewOrUpdatedState = (state == Parse.Model.HarvestState.New || state == Parse.Model.HarvestState.Updated);

			// This is an important exception-to-the-rule case for almost every scenario,
			// so let's get it out of the way first.
			bool isStaleState = false;
			if (state == Parse.Model.HarvestState.InProgress)
			{
				// In general, we just skip and let whoever else is working on it do its thing to avoid any potential for getting into strange states.
				// But if it's been "InProgress" for a suspiciously long time, it seems like it might've crashed. In that case, consider processing it.
				TimeSpan timeDifference = DateTime.UtcNow - book.HarvestStartedAt.UtcTime;
				if (timeDifference.TotalDays < 2)
				{
					reason = "SKIP: Recently in progress";
					return false;
				}
				else
				{
					isStaleState = true;
				}
			}

			if (harvestMode == HarvestMode.All)
			{
				// If settings say to process all books, this is easy. We always return true.
				reason = $"PROCESS: Mode = HarvestAll";
				return true;
			}
			else if (harvestMode == HarvestMode.NewOrUpdatedOnly)
			{
				if (isNewOrUpdatedState)
				{
					reason = "PROCESS: New or Updated state";
					return true;
				}
				else
				{
					reason = "SKIP: Not new or updated.";
					return false;
				}
			}
			else if (harvestMode == HarvestMode.RetryFailuresOnly)
			{
				Version previouslyUsedVersion = new Version(book.HarvesterMajorVersion, book.HarvesterMinorVersion);
				if (previouslyUsedVersion > currentVersion)
				{
					// A newer version previously marked it as failed. Don't touch this book.
					reason = "SKIP: Previously processed by newer version.";
					return false;
				}
				else
				{
					reason = "PROCESS: Retrying failure.";
					return true;
				}
			}
			else if (harvestMode == HarvestMode.Default)
			{
				Version previouslyUsedVersion = new Version(book.HarvesterMajorVersion, book.HarvesterMinorVersion);
				switch (state)
				{
					case Parse.Model.HarvestState.New:
					case Parse.Model.HarvestState.Updated:
					case Parse.Model.HarvestState.Unknown:
					default:
						reason = "PROCESS: New or Updated state";
						return true;
					case Parse.Model.HarvestState.Done:
						if (currentVersion.Major > book.HarvesterMajorVersion)
						{
							reason = "PROCESS: Updated major version, so updating output";
							return true;
						}
						else
						{
							reason = "SKIP: Already processed successfully.";
							return false;
						}
					case Parse.Model.HarvestState.Aborted:
						if (currentVersion >= previouslyUsedVersion)
						{
							reason = "PROCESS: Re-starting book that was previously aborted";
							return true;
						}
						else
						{
							reason = "SKIP: Skipping aborted book that was previously touched by a newer version.";
							return false;
						}
					case Parse.Model.HarvestState.InProgress:
						if (!isStaleState)
						{
							reason = "SKIP: Recently in progress";
							return false;
						}
						else if (currentVersion > previouslyUsedVersion)
						{
							reason = "PROCESS: Retrying stuck book of older version.";
							return true;
						}
						else if (currentVersion == previouslyUsedVersion)
						{
							reason = "PROCESS: Retrying stuck book of current version.";
							return true;
						}
						else
						{
							reason = "SKIP: Skipping stuck book that was previously processed by a newer version.";
							return false;
						}
					case Parse.Model.HarvestState.Failed:
						if (currentVersion > previouslyUsedVersion)
						{
							// Current is at least a minor version newer than what we had before
							// Default to true (re-try failures), unless we have reason to think that it's still pretty hopeless for the book to succeed.

							var previouslyMissingFontNames = book.GetMissingFonts();

							if (previouslyMissingFontNames.Any())
							{
								var stillMissingFontNames = FontChecker.GetMissingFonts(previouslyMissingFontNames);

								if (stillMissingFontNames.Any())
								{
									reason = $"SKIP: Still missing font {stillMissingFontNames.First()}";
									return false;
								}
							}

							reason = "PROCESS: Retry-ing failed book of older version.";
							return true;
						}
						else if (currentVersion == previouslyUsedVersion)
						{
							reason = "SKIP: Marked as failed by current version.";
							return false;
						}
						{
							reason = "SKIP: Marked as failed by newer version.";
							return false;
						}
				}
			}
			else
			{
				throw new ArgumentException("Unexpected mode: " + harvestMode);
			}
		}

		// Precondition: Assumes that baseUrl is not URL-encoded, and that it ends with the book title as a subfolder.
		public static string RemoveBookTitleFromBaseUrl(string baseUrl)
		{
			if (String.IsNullOrEmpty(baseUrl))
			{
				return baseUrl;
			}

			int length = baseUrl.Length;
			if (baseUrl.EndsWith("/"))
			{
				// Don't bother processing trailing slash
				--length;
			}

			int lastSlashIndex = baseUrl.LastIndexOf('/', length - 1);

			string urlWithoutTitle = baseUrl;
			if (lastSlashIndex >= 0)
			{
				urlWithoutTitle = baseUrl.Substring(0, lastSlashIndex);
			}

			return urlWithoutTitle;
		}

		// Returns list of log entries for missing fonts
		internal List<LogEntry> CheckForMissingFontErrors(string bookPath, Book book)
		{
			var harvestLogEntries = new List<LogEntry>();

			var missingFontsForCurrBook = _fontChecker.GetMissingFonts(bookPath, out bool success);

			if (!success)
			{
				// We now require successful determination of which fonts are missing.
				// Since we abort processing a book if any fonts are missing,
				// we don't want to proceed blindly if we're not sure if the book is missing any fonts.
				harvestLogEntries.Add(new LogEntry(LogLevel.Error, LogType.GetFontsError, "Error calling getFonts"));
				_issueReporter.ReportError("Error calling getMissingFonts", "", "", book.Model);
				return harvestLogEntries;
			}

			bool areAnyFontsMissing = missingFontsForCurrBook.Any();
			if (areAnyFontsMissing)
			{
				_logger.LogWarn("Missing fonts: " + String.Join(",", missingFontsForCurrBook));

				foreach (var missingFont in missingFontsForCurrBook)
				{
					var logEntry = new LogEntry(LogLevel.Error, LogType.MissingFont, message: missingFont);
					harvestLogEntries.Add(logEntry);

					if (!_missingFonts.Contains(missingFont))
					{
						_issueReporter.ReportMissingFont(missingFont, this.Identifier, book.Model);
						_missingFonts.Add(missingFont);
					}
					else
					{
						// We already know that this font is missing, which means we already reported an issue to YouTrack. No need to re-report it.
						Console.Out.WriteLine("Missing font, but no issue created because already known: " + missingFont);
					}
				}
			}

			return harvestLogEntries;
		}

		private bool CreateArtifacts(string downloadUrl, string collectionBookDir, string collectionFilePath, Book book, List<LogEntry> harvestLogEntries)
		{
			Debug.Assert(book != null, "CreateArtifacts(): book expected to be non-null");
			Debug.Assert(harvestLogEntries != null, "CreateArtifacts(): harvestLogEntries expected to be non-null");

			bool success = true;

			using (var folderForUnzipped = new TemporaryFolder(this.GetBloomDigitalArtifactsPath()))
			{
				using (var folderForZipped = new TemporaryFolder($"BHStaging-{this.GetUniqueIdentifier()}"))
				{
					var components = new S3UrlComponents(downloadUrl);
					// Add the book title as a subfolder to the path for unzipped so that Bloom can use its
					// trick to disambiguate two HTML files in the folder by comparing the filename to the
					// parent directory name.  (Having two HTML files in the uploaded book was one source
					// of multiple Harvester errors.)
					var bookTitleFileBasename = Bloom.Book.BookStorage.SanitizeNameForFileSystem(components.BookTitle);
					var baseForUnzipped = Path.Combine(folderForUnzipped.FolderPath, bookTitleFileBasename);

					string zippedBloomDOutputPath = Path.Combine(folderForZipped.FolderPath, $"{bookTitleFileBasename}.bloomd");
					string epubOutputPath = Path.Combine(folderForZipped.FolderPath, $"{bookTitleFileBasename}.epub");
					string thumbnailInfoPath = Path.Combine(folderForZipped.FolderPath, "thumbInfo.txt");

					string bloomArguments = $"createArtifacts \"--bookPath={collectionBookDir}\" \"--collectionPath={collectionFilePath}\"";
					if (!_options.SkipUploadBloomDigitalArtifacts || !_options.SkipUpdateMetadata)
					{
						// Note: We need bloomDigitalOutputPath if we update metadata too, because making the bloomd is what generates our updated meta.json
						bloomArguments += $" \"--bloomdOutputPath={zippedBloomDOutputPath}\" \"--bloomDigitalOutputPath={baseForUnzipped}\"";
					}

					if (!_options.SkipUploadEPub)
					{
						bloomArguments += $" \"--epubOutputPath={epubOutputPath}\"";
					}

					if (!_options.SkipUploadThumbnails)
					{
						bloomArguments += $" \"--thumbnailOutputInfoPath={thumbnailInfoPath}\"";
					}

					// Start a Bloom command line in a separate process
					var bloomCliStopwatch = new Stopwatch();
					bloomCliStopwatch.Start();
					bool exitedNormally = _bloomCli.StartAndWaitForBloomCli(bloomArguments, kCreateArtifactsTimeoutSecs * 1000, out int bloomExitCode, out string bloomStdOut, out string bloomStdErr);
					bloomCliStopwatch.Stop();

					string errorDescription = "";
					if (exitedNormally)
					{
						if (bloomExitCode == 0)
						{
							_logger.LogVerbose($"CreateArtifacts finished successfully in {bloomCliStopwatch.Elapsed.TotalSeconds:0.0} seconds.");
						}
						else
						{
							success = false;
							IEnumerable<string> errors = Bloom.CLI.CreateArtifactsCommand.GetErrorsFromExitCode(bloomExitCode) ?? Enumerable.Empty<string>();
							string errorInfo = String.Join(", ", errors);
							string errorMessage = $"Bloom Command Line error: CreateArtifacts failed with exit code: {bloomExitCode} ({errorInfo}).";
							errorDescription += errorMessage;

							harvestLogEntries.Add(new LogEntry(LogLevel.Error, LogType.BloomCLIError, errorMessage));
						}
					}
					else
					{
						success = false;
						string errorMessage = $"Bloom Command Line error: CreateArtifacts terminated because it exceeded {kCreateArtifactsTimeoutSecs} seconds.";
						errorDescription += errorMessage;
						harvestLogEntries.Add(new LogEntry(LogLevel.Error, LogType.TimeoutError, errorMessage));
					}

					if (success && !_options.SkipUploadBloomDigitalArtifacts)
					{
						string expectedIndexPath = Path.Combine(baseForUnzipped, "index.htm");
						if (!_fileIO.Exists(expectedIndexPath))
						{
							success = false;
							errorDescription += $"BloomDigital folder missing index.htm file";
							// ENHANCE: Maybe you could downgrade this to a warning, let it proceed, and then have the artifact suitability code check for this and mark Read Online suitability = false
							harvestLogEntries.Add(new LogEntry(LogLevel.Error, LogType.MissingBloomDigitalIndex, "Missing BloomDigital index.htm file"));
						}
					}

					string errorDetails = "";
					if (!success)
					{
						// Usually better just to report these right away. If BloomCLI didn't succeed, the subsequent Upload...() methods will probably throw an exception, except it'll be more confusing because it's not directly related to the root cause anymore.
						_logger.LogError(errorDetails);
						errorDetails += $"\n===StandardOut===\n{bloomStdOut ?? ""}\n";
						errorDetails += $"\n===StandardError===\n{bloomStdErr ?? ""}";
						_issueReporter.ReportError("Harvester BloomCLI Error", errorDescription, errorDetails, book.Model);
					}
					else
					{
						string s3FolderLocation = $"{components.Submitter}/{components.BookGuid}";

						if (!_options.SkipUploadBloomDigitalArtifacts)
						{
							UploadBloomDigitalArtifacts(zippedBloomDOutputPath, baseForUnzipped, s3FolderLocation);
						}

						if (!_options.SkipUploadEPub)
						{
							if (_fileIO.Exists(epubOutputPath))
							{
								UploadEPubArtifact(epubOutputPath, s3FolderLocation);
								_ePubExists = true;
							}
							else
							{
								harvestLogEntries.Add(new LogEntry(LogLevel.Warn, LogType.ArtifactSuitability, "Missing ePUB artifact: likely a comic book"));
								_ePubExists = false;
							}
						}

						if (!_options.SkipUploadThumbnails)
						{
							UploadThumbnails(book, thumbnailInfoPath, s3FolderLocation);
						}

						if (!_options.SkipUpdateMetadata)
						{
							book.UpdateMetadataIfNeeded(baseForUnzipped);
						}
					}
				}
			}

			return success;
		}

		/// <summary>
		/// The drive containing the downloaded books
		/// </summary>
		/// <returns></returns>
		private static IDriveInfo GetHarvesterDrive()
		{
			var fileInfo = new FileInfo(GetRootPath());
			var driveInfo = new DriveInfo(fileInfo.Directory.Root.FullName);
			return new HarvesterDriveInfo(driveInfo);
		}

		private static string GetRootPath()
		{
			string path = Environment.GetEnvironmentVariable("BloomHarvesterCacheRootPath");
			if (!string.IsNullOrEmpty(path))
			{
				if (!Directory.Exists(path))
				{
					try
					{
						Directory.CreateDirectory(path);
					}
					catch (Exception e)
					{
						Debug.WriteLine($"Cannot create {path} because {e}");
					}
				}
				if (Directory.Exists(path))
					return path;
			}
			// If no special setting, try for the user's local application directory as the root.
			path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (!String.IsNullOrEmpty(path) && Directory.Exists(path))
				return path;
			// If our attempt to find the user's home directory failed, use the temp directory,
			// assuming that the .Net/mono runtime knows how to return a good value for this.
			return Path.GetTempPath();
		}

		internal string GetBookCollectionPath()
		{
			// Note: This has the same problems as the next method for running multiple instances of
			// Harvester at the same time on the same computer.
			return Path.Combine(Path.GetTempPath(), "BloomHarvester", "Collection", this.Identifier);
		}

		internal string GetBookCacheFolder()
		{
			// Note: If there are multiple instances of the Harvester processing the same environment,
			//       and they both process the same book, they will attempt to download to the same path, which will probably be bad.
			//       But for now, the benefit of having each run download into a predictable location (allows caching when enabled)
			//       seems to outweigh the cost (since we don't normally run multiple instances w/the same env on same machine)
			return Path.Combine(GetRootPath(), "BloomHarvester", this.Identifier);
		}

		internal string GetBloomDigitalArtifactsPath()
		{
			return $"BHUnzipped-{this.GetUniqueIdentifier()}";
		}

		/// <summary>
		/// Uploads the .bloomd and the bloomdigital folders to S3
		/// </summary>
		/// <param name="zippedBloomDPath">The .bloomd file (zipped) path on this machine</param>
		/// <param name="unzippedFolderPath">The bloomdigital folder (unzipped) path on this machine</param>
		/// <param name="s3FolderLocation">The S3 path to upload to</param>
		private void UploadBloomDigitalArtifacts(string zippedBloomDPath, string unzippedFolderPath, string s3FolderLocation)
		{
			_logger.TrackEvent("Upload .bloomd");
			_s3UploadClient.UploadFile(zippedBloomDPath, s3FolderLocation, "no-cache");

			_logger.TrackEvent("Upload bloomdigital directory");
			// Clear out the directory first to make sure stale artifacts get removed.
			string folderToUploadTo = $"{s3FolderLocation}/bloomdigital";
			_s3UploadClient.DeleteDirectory(folderToUploadTo);
			_s3UploadClient.UploadDirectory(unzippedFolderPath, folderToUploadTo);
		}

		// This function doesn't wrap much, but I made so that when studying the stack trace of exceptions, we could distinguish errors uploading .bloomd vs .epub files.
		/// <summary>
		/// Uploads an EPub to S3
		/// </summary>
		/// <param name="epubPath">The current location of an ePub on this machine</param>
		/// <param name="s3FolderLocation">The S3 path to upload to</param>
		private void UploadEPubArtifact(string epubPath, string s3FolderLocation)
		{
			_logger.TrackEvent("Upload .epub");
			string folderToUploadTo = $"{s3FolderLocation}/epub";
			_s3UploadClient.DeleteDirectory(folderToUploadTo);
			_s3UploadClient.UploadFile(epubPath, folderToUploadTo, "no-cache");
		}

		/// <summary>
		/// Uploads the thumbnails to S3
		/// </summary>
		/// <param name="thumbnailInfoPath">This is a path to a TEXT file which contains information about where to find the actual thumbnails. The thumbnail paths should be written one per line in this file.</param>
		/// <param name="s3FolderLocation">The S3 path to upload to</param>
		private void UploadThumbnails(Book book, string thumbnailInfoPath, string s3FolderLocation)
		{
			string folderToUploadTo = $"{s3FolderLocation}/thumbnails";
			_s3UploadClient.DeleteDirectory(folderToUploadTo);

			if (_fileIO.Exists(thumbnailInfoPath))
			{
				// First parse the info file, which is NOT the actual thumbnail image bits. It just contains the filepath strings.
				string[] lines = _fileIO.ReadAllLines(thumbnailInfoPath);
				if (lines == null)
				{
					return;
				}

				bool wasSocialMediaThumbnailFound = false;
				foreach (var thumbnailPath in lines)
				{
					// These paths should point to the locations of the actual thumbnails. Upload them to S3.
					if (_fileIO.Exists(thumbnailPath))
					{
						_logger.TrackEvent("Upload thumbnail");
						_s3UploadClient.UploadFile(thumbnailPath, folderToUploadTo, "max-age=31536000");	// 60 * 60 * 24 * 365 = 1 year in seconds

						// Mark if the thumbnail to use when sharing to social media is generated and available.
						if (IsThumbnailForSocialMediaSharing(thumbnailPath))
						{
							wasSocialMediaThumbnailFound = true;
							
						}
					}
				}

				book.SetHarvesterEvaluation("social", wasSocialMediaThumbnailFound);
			}
		}

		private static bool IsThumbnailForSocialMediaSharing(string thumbnailPath)
		{
			return Path.GetFileNameWithoutExtension(thumbnailPath) == "thumbnail-300x300";
		}
	}
}
