using Bloom;
using Bloom.WebLibraryIntegration;

namespace BloomHarvester.WebLibraryIntegration
{
	internal interface IBookDownload
	{
		string HandleDownloadWithoutProgress(string url, string destRoot);
	}

	/// <summary>
	/// This class is basically just a wrapper around Bloom's version of BookTransfer
	/// that marks that it implements the IBookDownload interface (to make our unit testing life easier)
	/// </summary>
	class HarvesterBookDownload : BookDownload, IBookDownload
	{
		internal HarvesterBookDownload(BloomParseClient parseClient, BloomS3Client bloomS3Client)
			: base(parseClient, bloomS3Client, new Bloom.BookDownloadStartingEvent())
		{
		}

		public new string HandleDownloadWithoutProgress(string url, string destRoot)
		{
			// Just need to declare this as public instead of internal (interfaces...)
			return base.HandleDownloadWithoutProgress(url, destRoot);
		}
	}
}
