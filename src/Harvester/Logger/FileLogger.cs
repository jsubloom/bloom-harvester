using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BloomHarvester.Logger
{
	// A logger that writes the logged information to a file.
	class FileLogger : IMonitorLogger
	{
		private StreamWriter writer;

		public FileLogger(string filename)
		{
			writer = File.AppendText(filename);
		}

		public void Dispose()
		{
			writer.Close();
		}

		public void LogCritical(string messageFormat, params object[] args)
		{
			writer.WriteLine(GetTimeInfo() + "Log Critical: " + messageFormat, args);
			writer.Flush();
		}

		public void LogError(string messageFormat, params object[] args)
		{
			writer.WriteLine(GetTimeInfo() + "Log Error: " + messageFormat, args);
			writer.Flush();
		}

		public void LogInfo(string messageFormat, params object[] args)
		{
			writer.WriteLine(GetTimeInfo() + "Log Info: " + messageFormat, args);
			writer.Flush();
		}

		public void LogVerbose(string messageFormat, params object[] args)
		{
			writer.WriteLine(GetTimeInfo() + "Log Verbose: " + messageFormat, args);
			writer.Flush();
		}

		public void LogWarn(string messageFormat, params object[] args)
		{
			writer.WriteLine(GetTimeInfo() + "Log Warn: " + messageFormat, args);
			writer.Flush();
		}

		public void TrackEvent(string eventName)
		{
			writer.WriteLine($"{GetTimeInfo()}Event: {eventName}");
			writer.Flush();
		}

		private string GetTimeInfo()
		{
			return $"[{DateTime.Now.ToString()} (UTC{TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).Hours})] ";
		}
	}
}
