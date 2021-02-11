using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using BloomHarvester.WebLibraryIntegration;
using CommandLine;

[assembly: InternalsVisibleTo("BloomHarvesterTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]	// Needed for NSubstitute to create mock objects

namespace BloomHarvester
{
	// This class should just get the command line arguments parsed, pass it off to Harvester, then get out of the way as much as possible.
	class Program
	{
		// Command line arguments sample: "harvestAll --environment=dev --parseDBEnvironment=prod"
		//
		// Some presets that you might copy and paste in:
		// harvest --mode=all --environment=dev --parseDBEnvironment=local --suppressLogs --count=2
		// harvest --mode=warnings --environment=dev --parseDBEnvironment=local --suppressLogs
		// harvest --mode=all --environment=dev --parseDBEnvironment=local --suppressLogs "--queryWhere={ \"objectId\":\"38WdeYJ0yF\"}"
		// harvest --mode=all --environment=dev --parseDBEnvironment=dev --suppressLogs "--queryWhere={ \"objectId\":\"JUCL9OMOza\"}"
		// harvest --mode=all --environment=dev --parseDBEnvironment=dev --suppressLogs "--queryWhere={ \"title\":{\"$in\":[\"Vaccinations\",\"Fox and Frog\",\"The Moon and the Cap\"]}}"
		// harvest --mode=all --environment=dev --parseDBEnvironment=dev --suppressLogs "--queryWhere={ \"title\":{\"$regex\":\"^^A\"}}"	// Note that the "^" in the regex apparently needed to be escaped with another "^" before it. Not sure why...
		// harvest --mode=all --environment=dev --parseDBEnvironment=prod --suppressLogs "--queryWhere={ \"title\":{\"$regex\":\"^^A\"},\"tags\":\"bookshelf:Ministerio de Educación de Guatemala\"}"	// Note that the "^" in the regex apparently needed to be escaped with another "^" before it. Not sure why...
		// harvest --mode=all --environment=prod --parseDBEnvironment=prod "--queryWhere={ \"tags\":\"bookshelf:Resources for the Blind, Inc. (Philippines)\"}"
		// harvest --mode=default --environment=dev --parseDBEnvironment=dev --suppressLogs "--queryWhere={ \"objectId\":{\"$in\":[\"ze17yO6jIm\",\"v4YABQJLB2\"]}}"
		// harvest --mode=default --environment=dev --parseDBEnvironment=dev --suppressLogs "--queryWhere={ \"uploader\":{\"$in\":[\"SXsqpDHGKk\"]}}"
		// harvest --mode=all --environment=dev --parseDBEnvironment=dev  --suppressLogs "--queryWhere={ \"objectId\":\"zBsLInOzWG\"}" --skipUploadBloomDigitalArtifacts
		// Note that --mode=forceAll allows harvester to run again regardless of the book's current state.
		//
		// updateState --parseDBEnvironment=dev --id="ze17yO6jIm" --newState="InProgress"
		// Alternatively, you can use Parse API Console.
		//   * Request type: PUT
		//   * Endpoint: classes/books/{OBJECTID}
		//   * Query Parameters: {"updateSource":"bloomHarvester","harvestState":"{NEWSTATE}"}
		//
		// batchUpdateState --parseDBEnvironment=dev "--queryWhere={ \"harvestState\":\"Failed\"}" --newState="Unknown"
		//
		[STAThread]
		public static void Main(string[] args)
		{
			if (Environment.OSVersion.Platform != PlatformID.Win32NT)
			{
				// See  https://issues.bloomlibrary.org/youtrack/issue/BL-8475.
				Console.WriteLine("Harvester cannot run on Linux until we verify that phash computations always yield the same result on both Windows and Linux!");
				return;
			}

			// See https://github.com/commandlineparser/commandline for documentation about CommandLine.Parser
			var parser = new CommandLine.Parser((settings) =>
			{
				settings.CaseInsensitiveEnumValues = true;
				settings.CaseSensitive = false;
				settings.HelpWriter = Console.Error;
			});

			try
			{
				parser.ParseArguments<HarvesterOptions, UpdateStateInParseOptions, BatchUpdateStateInParseOptions, GenerateProcessedFilesTSVOptions>(args)
					.WithParsed<HarvesterOptions>(options =>
					{
						options.ValidateOptions();
						Harvester.RunHarvest(options);
					})
					.WithParsed<UpdateStateInParseOptions>(options =>
					{
						HarvestStateUpdater.UpdateState(options.ParseDBEnvironment, options.ObjectId, options.NewState);
					})
					.WithParsed<BatchUpdateStateInParseOptions>(options =>
					{
						HarvestStateBatchUpdater.RunBatchUpdateStates(options);
					})
					.WithParsed<GenerateProcessedFilesTSVOptions>(options =>
					{
						ProcessedFilesInfoGenerator.GenerateTSV(options);
					})
					.WithNotParsed(errors =>
					{
						Console.Out.WriteLine("Error parsing command line arguments.");
						Environment.Exit(1);
					});
			}
			catch (Exception e)
			{
				YouTrackIssueConnector.GetInstance(EnvironmentSetting.Unknown).ReportException(e, "An exception was thrown which was not handled by the program.", null);
				throw;
			}
		}
	}

	[Verb("harvest", HelpText = "Run Harvester on a set of books")]
	public class HarvesterOptions
	{
		[Option("mode", Required = true, HelpText = "Which mode to run Harvester in, e.g. \"Default\", \"All\", \"HarvestNeeded\"")]
		public HarvestMode Mode { get; set; }

		[Option('e', "environment", Required = false, Default = EnvironmentSetting.Dev, HelpText = "Sets all environments to read/write from. Valid values are Default, Dev, Test, or Prod. If any individual component's environment are set to non-default, that value will take precedence over this.")]
		public EnvironmentSetting Environment { get; set; }

		[Option("parseDBEnvironment", Required = false, Default = EnvironmentSetting.Default, HelpText = "Sets the environment to read/write from Parse DB. Valid values are Default, Dev, Test, or Prod. If specified (to non-Default), takes precedence over the general 'environment' option.")]
		public EnvironmentSetting ParseDBEnvironment { get; set; }

		[Option("logEnvironment", Required = false, Default = EnvironmentSetting.Default, HelpText = "Sets the environment to read/write from the logging resource. Valid values are Default, Dev, Test, or Prod. If specified (to non-Default), takes precedence over the general 'environment' option.")]
		public EnvironmentSetting LogEnvironment { get; set; }

		// ENHANCE: Perhaps this parameter should also suppress creating YouTrack issues?
		[Option("suppressLogs", Required = false, Default = false, HelpText = "If true, will prevent log messages from being logged to the log environment (which may incur fees). Will write those logs to Standard Error instead.")]
		public bool SuppressLogs { get; set; }

		[Option("suppressErrors", Required = false, Default = false, HelpText = "If true, will prevent errors from being reported to the issue tracking system")]
		public bool SuppressErrors{ get; set; }

		[Option("queryWhere", Required = false, Default = "", HelpText = "If specified, adds a WHERE clause to the request query when retrieving the list of books to process. This should be in the JSON format used by Parse REST API to pass WHERE clauses. See https://docs.parseplatform.org/rest/guide/#query-constraints")]
		public string QueryWhere { get; set; }

		[Option("readOnly", Required = false, Default = false, HelpText = "If specified, harvester just downloads the books")]
		public bool ReadOnly { get; set; }

		[Option("count", Required = false, Default = -1, HelpText = "The amount of records to process. Default -1. If specified to a positive value, then processing will end after processing the specified number of books.")]
		public int Count { get; set; }

		[Option("loop", Required = false, Default = false, HelpText = "If true, will keep re-running Harvester after it finishes.")]
		public bool Loop { get; set; }

		[Option("loopWaitSeconds", Required = false, Default = 300, HelpText = "If specified, and loop mode is on, then specifies the number of seconds to wait between loop iterations if nothing was previously processed")]
		public int LoopWaitSeconds { get; set; }

		[Option("forceDownload", Required = false, Default = false,  HelpText = "If true, will force the re-downloading of a book, even if it already exists.")]
		public bool ForceDownload { get; set; }

		[Option("skipDownload", Required = false, Default = false, HelpText = "If true, will skip downloading the book but ONLY if it already exists.")]
		public bool SkipDownload { get; set; }

		[Option("skipUploadBloomDigitalArtifacts", Required = false, Default = false, HelpText = "If true, will prevent the .bloomd and Bloom Digital (Read on Bloom Library) artifacts from being uploaded.")]
		public bool SkipUploadBloomDigitalArtifacts { get; set; }

		[Option("skipUploadEPub", Required = false, Default = false, HelpText = "If true, will prevent the .epub artifact from being uploaded.")]
		public bool SkipUploadEPub { get; set; }

		[Option("skipUploadThumbnails", Required = false, Default = false, HelpText = "If true, will prevent new thumbnails from being created and uploaded.")]
		public bool SkipUploadThumbnails { get; set; }

		[Option("skipUpdatePerceptualHash", Required = false, Default = false, HelpText = "If true, will prevent perceptual hash from being created and uploaded.")]
		public bool SkipUpdatePerceptualHash{ get; set; }

		[Option("skipUpdateMetadata", Required = false, Default = false, HelpText = "If true, will skip updating the metadata (e.g. Features field) in Parse.")]
		public bool SkipUpdateMetadata { get; set; }

		public virtual string GetPrettyPrint()
		{
			return $"mode: {Mode}\n" +
				$"environment: {Environment}\n" +
				$"parseDBEnvironment: {ParseDBEnvironment}\n" +
				$"logEnvironment: {LogEnvironment}\n" +
				$"suppressLogs: {SuppressLogs}\n" +
				$"queryWhere: {QueryWhere}\n" +
				$"count: {Count}\n" +
				$"skipUploadBloomDigitalArtifacts: {SkipUploadBloomDigitalArtifacts}\n" +
				$"skipUploadEPub: {SkipUploadEPub}";
		}

		public void ValidateOptions()
		{
			if (ForceDownload && SkipDownload)
			{
				throw new ArgumentException("ForceDownload and SkipDownload are mutually exclusive, but both were set.");
			}
		}
	}

	[Verb("updateState", HelpText = "Updates the harvestState field in Parse")]
	public class UpdateStateInParseOptions
	{
		[Option("parseDBEnvironment", Required = false, Default = EnvironmentSetting.Default, HelpText = "Sets the environment to read/write from Parse DB. Valid values are Default, Dev, Test, or Prod. If specified (to non-Default), takes precedence over the general 'environment' option.")]
		public EnvironmentSetting ParseDBEnvironment { get; set; }

		[Option("id", Required = true, HelpText = "The objectId of the item to update.")]
		public string ObjectId { get; set; }

		[Option("newState", Required = true, HelpText = "The new state to set it to")]
		public Parse.Model.HarvestState NewState { get; set; }
	}

	[Verb("batchUpdateState", HelpText = "Batch updates the harvestState field in Parse")]
	public class BatchUpdateStateInParseOptions
	{
		[Option("parseDBEnvironment", Required = false, Default = EnvironmentSetting.Default, HelpText = "Sets the environment to read/write from Parse DB. Valid values are Default, Dev, Test, or Prod. If specified (to non-Default), takes precedence over the general 'environment' option.")]
		public EnvironmentSetting ParseDBEnvironment { get; set; }

		[Option("queryWhere", Required = true, Default = "", HelpText = "If specified, adds a WHERE clause to the request query when retrieving the list of books to process. This should be in the JSON format used by Parse REST API to pass WHERE clauses. See https://docs.parseplatform.org/rest/guide/#query-constraints")]
		public string QueryWhere { get; set; }

		[Option("newState", Required = true, HelpText = "The new state to set it to")]
		public Parse.Model.HarvestState NewState { get; set; }

		[Option("dryRun", Required = false, Default = false, HelpText = "If specified, will print out what it will try to do, but will not actually execute the commands.")]
		public bool DryRun { get; set; }
	}

	[Verb("generateProcessedFilesTSV", HelpText = "Generates a TSV file containing the processed files")]
	public class GenerateProcessedFilesTSVOptions
	{
		[Option('e', "environment", Required = false, Default = EnvironmentSetting.Dev, HelpText = "Sets all environments to read/write from. Valid values are Default, Dev, Test, or Prod. If any individual component's environment are set to non-default, that value will take precedence over this.")]
		public EnvironmentSetting Environment { get; set; }

		[Option("parseDBEnvironment", Required = false, Default = EnvironmentSetting.Default, HelpText = "Sets the environment to read/write from Parse DB. Valid values are Default, Dev, Test, or Prod. If specified (to non-Default), takes precedence over the general 'environment' option.")]
		public EnvironmentSetting ParseDBEnvironment { get; set; }

		[Option("queryWhere", Required = false, Default = "", HelpText = "If specified, adds a WHERE clause to the request query when retrieving the list of books to process. This should be in the JSON format used by Parse REST API to pass WHERE clauses. See https://docs.parseplatform.org/rest/guide/#query-constraints")]
		public string QueryWhere { get; set; }

		[Option("outputPath", Required = true, HelpText = "Location of the output file")]
		public string OutputPath { get; set; }
	}
}
