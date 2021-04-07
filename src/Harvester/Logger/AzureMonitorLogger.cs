using BloomHarvester.WebLibraryIntegration;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using SIL.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BloomHarvester.Logger
{
	/// <summary>
	/// Implementation of a logger which sends data to Azure Monitor / Azure Application Insights.
	/// </summary>
	class AzureMonitorLogger : IMonitorLogger, IDisposable
	{
		private TelemetryClient _telemetry = new TelemetryClient();
		private IMonitorLogger _fileLogger; // log to both Azure as well as something on the local filesystem, which would have more real-time access
		private IIssueReporter _issueReporter;

		public AzureMonitorLogger(EnvironmentSetting environment, string harvesterId)
		{
			_issueReporter = YouTrackIssueConnector.GetInstance(environment);

			// Get the Instrumentation Key for Azure from an environment variable.
			string environmentVarName = "BloomHarvesterAzureAppInsightsKeyDev";
			if (environment == EnvironmentSetting.Test)
			{
				environmentVarName = "BloomHarvesterAzureAppInsightsKeyTest";
			}
			else if (environment == EnvironmentSetting.Prod)
			{
				environmentVarName = "BloomHarvesterAzureAppInsightsKeyProd";
			}

			string instrumentationKey = Environment.GetEnvironmentVariable(environmentVarName);
			Debug.Assert(!String.IsNullOrWhiteSpace(instrumentationKey), "Azure Instrumentation Key is invalid. Azure logging probably won't work.");

			try
			{
				Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration.Active.InstrumentationKey = instrumentationKey;
			}
			catch (ArgumentNullException e)
			{
				_issueReporter.ReportException(e, $"InstrumentationKey: {instrumentationKey ?? "null"}.\nenvironmentVarName: {environmentVarName}", null);
			}

			_telemetry.Context.User.Id = "BloomHarvester " + harvesterId;
			_telemetry.Context.Session.Id = Guid.NewGuid().ToString();
			_telemetry.Context.Device.OperatingSystem = Environment.OSVersion.ToString();

			string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BloomHarvester", "log.txt");
			Console.Out.WriteLine("Creating log file at: " + logFilePath);
			try
			{
				if (File.Exists(logFilePath))
				{
					// Check if the file is too big (~10 MB)
					if (new FileInfo(logFilePath).Length > 10000000)
					{
						var oldPath = logFilePath + "-OLD";
						// Preserve one previous log file for debugging help, and start over with
						// an empty log file.
						// (The data is in Azure too anyway, but having it local may speed things up.)
						if (RobustFile.Exists(oldPath))
							RobustFile.Delete(oldPath);
						RobustFile.Move(logFilePath, oldPath);
					}
				}
			}
			catch
			{
				// Doesn't matter if there are any errors
			}

			try
			{
				_fileLogger = new FileLogger(logFilePath);
			}
			catch
			{
				// That's unfortunate that creating the logger failed, but I don't really want to throw an exception since the file logger isn't even the main purpose of this calss.
				// Let's just replace it with something to get it to be quiet.
				_fileLogger = new NullLogger();
			}
		}

		#region IMonitorLogger
		/// <summary>
		/// Very important that Dispose() should be called before the program concludes, or else events/telemetry may not be persisted to remote storage!
		/// </summary>
		public void Dispose()
		{
			_fileLogger.Dispose();
			_telemetry.Flush();
			Console.Out.WriteLine("Flushing AzureMonitor");
			System.Threading.Thread.Sleep(5000);    // Allow some time to flush before shutdown. It might not flush right away. (https://docs.microsoft.com/en-us/azure/azure-monitor/app/api-custom-events-metrics#tracktrace)
		}

		// Log a trace with Severity = Critical
		public void LogCritical(string messageFormat, params object[] args)
		{
			this.TrackTrace(SeverityLevel.Critical, messageFormat, args);
			_fileLogger.LogCritical(messageFormat, args);
		}

		// Log a trace with Severity = Error
		public void LogError(string messageFormat, params object[] args)
		{
			this.TrackTrace(SeverityLevel.Error, messageFormat, args);
			_fileLogger.LogError(messageFormat, args);
		}

		// Log a trace with Severity = Warning
		public void LogWarn(string messageFormat, params object[] args)
		{
			this.TrackTrace(SeverityLevel.Warning, messageFormat, args);
			_fileLogger.LogWarn(messageFormat, args);
		}

		// Log a trace with Severity = Information
		public void LogInfo(string messageFormat, params object[] args)
		{
			this.TrackTrace(SeverityLevel.Information, messageFormat, args);
			_fileLogger.LogInfo(messageFormat, args);
		}

		// Log a trace with Severity = Verbose
		public void LogVerbose(string messageFormat, params object[] args)
		{
			this.TrackTrace(SeverityLevel.Verbose, messageFormat, args);
			_fileLogger.LogVerbose(messageFormat, args);
		}

		// Suggest that this be used to log more significant things like actions to the Events table as opposed to Traces which can represent diagnostic/debugging log messages
		public void TrackEvent(string eventName)
		{
			_telemetry.TrackEvent(eventName);
			_fileLogger.TrackEvent(eventName);
			Console.Error.WriteLine($"Event: {eventName}");
		}
		#endregion


		protected void TrackTrace(SeverityLevel severityLevel, string messageFormat, params object[] args)
		{
			_telemetry.TrackTrace(String.Format(messageFormat, args), severityLevel);
			Console.Error.WriteLine($"Log {severityLevel}: " + messageFormat, args);
		}
	}
}
