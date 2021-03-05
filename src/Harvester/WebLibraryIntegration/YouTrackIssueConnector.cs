using BloomHarvester.Parse.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;


namespace BloomHarvester.WebLibraryIntegration
{
	internal interface IIssueReporter
	{
		bool Disabled { get; set; }
		void ReportException(Exception exception, string additionalDescription, BookModel bookModel, bool exitImmediately = true);
		void ReportError(string errorSummary, string errorDescription, string errorDetails, BookModel bookModel = null);
		void ReportMissingFont(string missingFontName, string harvesterId, BookModel bookModel = null);
	}

	internal class YouTrackIssueConnector : IIssueReporter
	{
		private YouTrackIssueConnector(EnvironmentSetting environment, string projectKey)
		{
			this.EnvironmentSetting = environment;
			_youTrackProjectKeyErrors = projectKey;
			_youTrackProjectKeyMissingFonts = projectKey;
		}

		private readonly string _youTrackProjectKeyErrors = "BH";  // Or "SB" for Sandbox
		private readonly string _youTrackProjectKeyMissingFonts = "BH";  // Or "SB" for Sandbox

		public EnvironmentSetting EnvironmentSetting { get; set; }

		public bool Disabled { get; set; } // Should default to Not Disabled

		private static YouTrackIssueConnector _instance;

		// Singleton Instance
		public static YouTrackIssueConnector GetInstance(EnvironmentSetting environment, string projectKey = "BH")
		{
			if (_instance == null || _instance.EnvironmentSetting != environment)
				_instance = new YouTrackIssueConnector(environment, projectKey);
			return _instance;
		}

		// This struct and the following list of structs is used only for testing (EnvironmentSetting.Test).
		internal struct ErrorReport
		{
			public string ProjectKey;
			public string Summary;
			public string Description;
		}
		internal List<ErrorReport> TestErrorReports = new List<ErrorReport>();

		private void ReportToYouTrack(string projectKey, string summary, string description, bool exitImmediately, BookModel bookModel = null)
		{
			Console.Error.WriteLine("ERROR: " + summary);
			Console.Error.WriteLine("==========================");
			Console.Error.WriteLine(description);
			Console.Error.WriteLine("==========================");
			Console.Error.WriteLine("==========================");
			if (EnvironmentSetting == EnvironmentSetting.Test)
			{
				TestErrorReports.Add(new ErrorReport
					{ProjectKey = projectKey, Summary = summary, Description = description});
				return;
			}
#if DEBUG
			Console.Out.WriteLine("***Issue caught but skipping creating YouTrack issue because running in DEBUG mode.***");
#else
			if (Disabled)
			{
				Console.Out.WriteLine("***Issue caught but skipping creating YouTrack issue because error reporting to YouTrack is disabled.***");
			}
			else
			{
				string youTrackIssueId = SubmitToYouTrack(summary, description, projectKey, bookModel);
				if (!String.IsNullOrEmpty(youTrackIssueId))
				{
					Console.Out.WriteLine($"Created YouTrack issue {youTrackIssueId}");
				}
			}
#endif

			if (exitImmediately)
			{
				// Exit immediately can avoid a couple awkward situations
				// If you don't exit the program, the immediate caller (which first caught the exception) could...
				// 1) re-throw the exception as is.  Then an even earlier caller might catch the re-thrown exception and call this again, writing the same issue multiple times.
				// 2) The caller might not attempt to throw or otherwise cause the premature termination of the program. If you run on all books, then you could have thousands of issues per run in the issue tracker.
				Environment.Exit(2);
			}
		}

		internal static string SubmitToYouTrack(string summary, string description, string youTrackProjectKey, BookModel bookModel = null)
		{
			bool isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(bookModel);
			if (isSilenced)
			{
				// Alerts are silenced because too many alerts.
				// Skip creating the YouTrack issue for this.
				return "";
			}
			var submitter = new Bloom.YouTrackIssueSubmitter(youTrackProjectKey);
			return submitter.SubmitToYouTrack(summary, description);
		}

		private string FixTitleForSummary(BookModel bookModel)
		{
			if (String.IsNullOrEmpty(bookModel?.Title?.Trim()))
				return String.Empty;
			return $" Title: \"{bookModel.Title.Replace('\n',' ').Replace('\r',' ').Trim()}\" ";
		}

		public void ReportException(Exception exception, string additionalDescription, BookModel bookModel, bool exitImmediately = true)
		{
			string summary = $"[BH] [{this.EnvironmentSetting}]{FixTitleForSummary(bookModel)} Exception: \"{exception.Message}\"";
			string description =
				additionalDescription + "\n\n" +
				GetDiagnosticInfo(bookModel, this.EnvironmentSetting) + "\n\n" +
				GetIssueDescriptionFromException(exception);

			ReportToYouTrack(_youTrackProjectKeyErrors, summary, description, exitImmediately, bookModel);
		}

		private static string GetIssueDescriptionFromException(Exception exception)
		{
			StringBuilder bldr = new StringBuilder();
			if (exception != null)
			{
				bldr.AppendLine("# Exception Info");    // # means Level 1 Heading in markdown.
				string exceptionInfo = exception.ToString();
				bldr.AppendLine(exception.ToString());

				string exceptionType = exception.GetType().ToString();
				if (exceptionInfo == null || !exceptionInfo.Contains(exceptionType))
				{
					// Just in case the exception info didn't already include the exception message. (The base class does, but derived classes aren't guaranteed)
					bldr.AppendLine();
					bldr.AppendLine(exceptionType);
				}


				if (exceptionInfo == null || !exceptionInfo.Contains(exception.Message))
				{
					// Just in case the exception info didn't already include the exception message. (The base class does, but derived classes aren't guaranteed)
					bldr.AppendLine();
					bldr.AppendLine("# Exception Message");
					bldr.AppendLine(exception.Message);
				}

				if (exceptionInfo == null || !exceptionInfo.Contains(exception.StackTrace))
				{
					// Just in case the exception info didn't already include the stack trace. (The base class does, but derived classes aren't guaranteed)
					bldr.AppendLine();
					bldr.AppendLine("# Stack Trace");
					bldr.AppendLine(exception.StackTrace);
				}
			}

			return bldr.ToString();
		}

		public void ReportError(string errorSummary, string errorDescription, string errorDetails, BookModel bookModel = null)
		{
			string summary = $"[BH] [{this.EnvironmentSetting}]{FixTitleForSummary(bookModel)} Error: {errorSummary}";
			string description =
				errorDescription + '\n' +
				'\n' +
				GetDiagnosticInfo(bookModel, this.EnvironmentSetting) + '\n' +
				errorDetails;

			ReportToYouTrack(_youTrackProjectKeyErrors, summary, description, exitImmediately: false, bookModel: bookModel);
		}

		public void ReportMissingFont(string missingFontName, string harvesterId, BookModel bookModel = null)
		{
			string summary = $"[BH] [{this.EnvironmentSetting}]{FixTitleForSummary(bookModel)} Missing Font: \"{missingFontName}\"";

			string description = $"Missing font \"{missingFontName}\" on machine \"{harvesterId}\".\n\n";
			description += GetDiagnosticInfo(bookModel, this.EnvironmentSetting);

			ReportToYouTrack(_youTrackProjectKeyMissingFonts, summary, description, exitImmediately: false, bookModel: bookModel);
		}

		private static string GetDiagnosticInfo(BookModel bookModel, EnvironmentSetting environment)
		{
			var assemblyVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version ?? new Version(0, 0);

			return
				(bookModel == null ? "" : bookModel.GetBookDiagnosticInfo(environment) + '\n') + 
				$"Environment: {environment}\n" +
				$"Harvester Version: {assemblyVersion.Major}.{assemblyVersion.Minor}\n" +
				$"Time: {DateTime.UtcNow.ToUniversalTime()} (UTC)";
		}
	}
}
