using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using BloomHarvester.Logger;
using BloomHarvester.Parse.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace BloomHarvester.Parse
{
	public interface IParseClient
	{
		/// <summary>
		/// Creates an object in a Parse class (table)
		/// This method may take about half a second to complete.
		/// </summary>
		/// <param name="className">The name of the class (table). Do not prefix it with "classes/"</param>
		/// <param name="json">The JSON of the object to write</param>
		/// <exception>Throws an application exception if the request fails</exception>
		/// <returns>The response after executing the request</returns>
		IRestResponse CreateObject(string className, string json);

		/// <summary>
		/// Updates an object in a Parse class (table)
		/// This method may take about half a second to complete.
		/// </summary>
		/// <param name="className">The name of the class (table). Do not prefix it with "classes/"</param>
		/// <param name="json">The JSON of the object to update. It doesn't need to be the full object, just of the fields to update</param>
		/// <exception>Throws an application exception if the request fails</exception>
		/// <returns>The response after executing the request</returns>
		IRestResponse UpdateObject(string className, string objectId, string updateJson);

		/// <summary>
		/// Deletes an object in a Parse class (table)
		/// </summary>
		/// <param name="className">The name of the class (table). Do not prefix it with "classes/"</param>
		/// <param name="objectId">The objectId of the object to delte</param>
		/// <exception>Throws an application exception if the request fails</exception>
		/// <returns>The response after executing the request</returns>
		IRestResponse DeleteObject(string className, string objectId);

		/// <summary>
		/// Gets all rows from the Parse "books" class/table
		/// </summary>
		List<BookModel> GetBooks(out bool didExitPrematurely, string whereCondition = "", IEnumerable<string> fieldsToDereference = null);
	}
	// Enhance: In the future, may be worth it to subclass the BloomDesktop one and take a dependency on it.
	// We could get the URLs and ApplicationID from the BlookDesktop base class. We would add our new Harvester-specific functions in our derived class.
	//
	// Right now, there's not enough common functionality to make it worthwhile. Maybe revisit this topic when the code becomes more stable.
	internal class ParseClient : Bloom.WebLibraryIntegration.BloomParseClient, IParseClient
	{
		// Constructors
		internal ParseClient(EnvironmentSetting environment, IMonitorLogger logger)
			:base()
		{
			_environmentSetting = environment;
			_client = CreateRestClient(environment);
			this.ApplicationId = GetApplicationId(environment);
			Debug.Assert(!String.IsNullOrWhiteSpace(ApplicationId), "Parse Application ID is invalid. Retrieving books from Parse probably won't work. Consider checking your environment variables.");

			Logger = logger;
		}

		private static RestClient CreateRestClient(EnvironmentSetting environment)
		{
			string url;
			switch (environment)
			{
				case EnvironmentSetting.Prod:
					// This URL gets redirected. It seems to work for reading, but not for writing. For that, we need the direct one.
					//url = "https://parse.bloomlibrary.org/";
					url = "https://bloom-parse-server-production.azurewebsites.net/parse";
					break;
				case EnvironmentSetting.Test:
					url = "https://bloom-parse-server-unittest.azurewebsites.net/parse";
					break;
				case EnvironmentSetting.Dev:
				default:
					//url = "https://dev-parse.bloomlibrary.org/";	// Theoretically, this link should work too and is preferred, but it doesn't work in the program for some reason even though it redirects correctly in the browser.
					url = "https://bloom-parse-server-develop.azurewebsites.net/parse";
					break;
				case EnvironmentSetting.Local:
					url = "http://localhost:1337/parse";
					break;
			}

			return new RestClient(url);
		}

		private static string GetApplicationId(EnvironmentSetting environment)
		{
			string appIdEnvVarKey;

			switch (environment)
			{
				case EnvironmentSetting.Prod:
					appIdEnvVarKey = "BloomHarvesterParseAppIdProd";
					break;
				case EnvironmentSetting.Test:
					appIdEnvVarKey = "BloomHarvesterParseAppIdTest";
					break;
				case EnvironmentSetting.Dev:
				default:
					appIdEnvVarKey = "BloomHarvesterParseAppIdDev";
					break;
				case EnvironmentSetting.Local:
					appIdEnvVarKey = null;
					break;
			}

			string applicationId = "myAppId";
			if (environment != EnvironmentSetting.Local)
			{
				applicationId = Environment.GetEnvironmentVariable(appIdEnvVarKey);
			}

			return applicationId;
		}

		// Fields and properties
		private EnvironmentSetting _environmentSetting;

		private const int kMaxBatchOpsToSend = 50;
		private List<BatchableOperation> _batchableOperations = new List<BatchableOperation>(kMaxBatchOpsToSend);

		// Careful! Very well might be null
		private Logger.IMonitorLogger Logger { get; set; }


		// Methods
		/// <summary>
		/// Logs in, if necessary
		/// </summary>
		private void EnsureLogIn()
		{
			if (String.IsNullOrEmpty(_sessionToken))
			{
				LogIn();
			}
		}

		/// <summary>
		/// Sends a request to log in
		/// </summary>
		private void LogIn()
		{
			_sessionToken = String.Empty;

			// The officially recommended way to pass the password is via a URL parameter of a GET request
			// (https://docs.parseplatform.org/rest/guide/#logging-in)
			// If you connect via HTTPS, it should get a secure TCP connection first, then send the HTTP request over SSL...
			// Which means that having the password in plaintext in the URL (which is at the HTTP layer) isn't immediately terrible.
			// Other than the fact that the server could very well want to log every URL it processes into a log... or print it out in an exception...
			// Or that you don't need https for localhost..
			var request = MakeRequest("login", Method.GET);
			request.AddParameter("username", Environment.GetEnvironmentVariable($"BloomHarvesterUserName").ToLowerInvariant());
			request.AddParameter("password", Environment.GetEnvironmentVariable($"BloomHarvesterUserPassword{_environmentSetting}"));

			var response = this.Client.Execute(request);
			CheckForResponseError(response, "Failed to log in");

			var dy = JsonConvert.DeserializeObject<dynamic>(response.Content);
			_sessionToken = dy.sessionToken; //there's also an "error" in there if it fails, but a null sessionToken tells us all we need to know
		}

		/// <summary>
		/// Makes a request of the specified type, and sets common headers and authentication tokens
		/// </summary>
		/// <param name="path">The Parse relative path, e.g. "classes/{className}"</param>
		/// <param name="requestType">e.g., GET, POST, etc.</param>
		/// <returns></returns>
		private RestRequest MakeRequest(string path, Method requestType)
		{
			var request = new RestRequest(path, requestType);
			SetCommonHeaders(request);
			return request;
		}

		private void SetCommonHeaders(RestRequest request)
		{
			request.AddHeader("X-Parse-Application-Id", ApplicationId);
			if (!string.IsNullOrEmpty(_sessionToken))
			{
				request.AddHeader("X-Parse-Session-Token", _sessionToken);
			}
		}

		private void AddJsonToRequest(RestRequest request, string json)
		{
			request.AddParameter("application/json", json, ParameterType.RequestBody);
		}

		/// <summary>
		/// Creates an object in a Parse class (table)
		/// This method may take about half a second to complete.
		/// </summary>
		/// <param name="className">The name of the class (table). Do not prefix it with "classes/"</param>
		/// <param name="json">The JSON of the object to write</param>
		/// <exception>Throws an application exception if the request fails</exception>
		/// <returns>The response after executing the request</returns>
		public IRestResponse CreateObject(string className, string json)
		{
			EnsureLogIn();
			var request = MakeRequest($"classes/{className}", Method.POST);
			AddJsonToRequest(request, json);

			var response = this.Client.Execute(request);
			CheckForResponseError(response, "Create failed.\nRequest.Json: {0}", json);

			return response;
		}

		/// <summary>
		/// Updates an object in a Parse class (table)
		/// This method may take about half a second to complete.
		/// </summary>
		/// <param name="className">The name of the class (table). Do not prefix it with "classes/"</param>
		/// <param name="json">The JSON of the object to update. It doesn't need to be the full object, just of the fields to update</param>
		/// <exception>Throws an application exception if the request fails</exception>
		/// <returns>The response after executing the request</returns>
		public IRestResponse UpdateObject(string className, string objectId, string updateJson)
		{
			EnsureLogIn();
			var request = MakeRequest($"classes/{className}/{objectId}", Method.PUT);
			AddJsonToRequest(request, updateJson);

			var response = this.Client.Execute(request);
			CheckForResponseError(response, "Update failed.\nRequest.Json: {0}", updateJson);

			return response;
		}

		/// <summary>
		/// Deletes an object in a Parse class (table)
		/// This method may take about half a second to complete.
		/// </summary>
		/// <param name="className">The name of the class (table). Do not prefix it with "classes/"</param>
		/// <param name="objectId">The objectId of the object to delte</param>
		/// <exception>Throws an application exception if the request fails</exception>
		/// <returns>The response after executing the request</returns>
		public IRestResponse DeleteObject(string className, string objectId)
		{
			EnsureLogIn();
			var request = MakeRequest($"classes/{className}/{objectId}", Method.DELETE);

			var response = this.Client.Execute(request);
			CheckForResponseError(response, "Delete of object {0} failed.", objectId);

			return response;
		}

		private void CheckForResponseError(IRestResponse response, string exceptionInfoFormat, params object[] args)
		{
			if (!IsResponseCodeSuccess(response.StatusCode))
			{
				throw new ParseException(response, exceptionInfoFormat + "\n", args);
			}
		}

		private static bool IsResponseCodeSuccess(HttpStatusCode statusCode)
		{
			return ((int)statusCode >= 200) && ((int)statusCode <= 299);
		}

		private static string GetResponseSummary(IRestResponse response, bool includeUri = true)
		{
			string summary =
				$"Response.Code: {response.StatusCode}\n" +
				(includeUri ? $"Response.Uri: {response.ResponseUri}\n" : "") +
				$"Response.Description: {response.StatusDescription}\n" +
				$"Response.Content: {response.Content}\n";
			return summary;
		}

		/// <summary>
		/// Gets all rows from the Parse "books" class/table
		/// </summary>
		public List<BookModel> GetBooks(out bool didExitPrematurely, string whereCondition = "", IEnumerable<string> fieldsToDereference = null)
		{
			var request = new RestRequest("classes/books", Method.GET);
			SetCommonHeaders(request);
			string keysToRetrieve = String.Join(",", BookModel.GetParseKeys());
			request.AddParameter("keys", keysToRetrieve);

			if (!String.IsNullOrEmpty(whereCondition))
			{
				request.AddParameter("where", whereCondition);
			}

			// Instead of representing as object pointers (and requiring us to perform a 2nd query to get the object), Parse will dereference the pointer for us automatically
			if (fieldsToDereference != null)
			{
				foreach (var field in fieldsToDereference)
				{
					request.AddParameter("include", field);
				}
			}

			List<BookModel> results = GetAllResults<BookModel>(request, out didExitPrematurely);
			results.ForEach(book => book.MarkAsDatabaseVersion());

			Console.Out.WriteLine($"{results.Count} matching results.");
			return results;
		}
				
		/// <summary>
		/// Lazily gets all the results from a Parse database in chunks
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="request">The request should not include count, limit, skip, or order fields. This method will populate those in order to provide the functionality</param>
		/// <returns>Yields the results through an IEnumerable as needed</returns>
		private List<T> GetAllResults<T>(IRestRequest request, out bool didExitPrematurely)
		{
			var results = new List<T>();

			int numProcessed = 0;
			int totalCount = 0;
			do
			{
				// Make sure you don't have duplicate instances of a lot of these parameters, especially limit and skip.
				// Parse will not give you the results you want if you have them multiple times.
				AddOrReplaceParameter(request, "count", "1");
				AddOrReplaceParameter(request, "limit", "10000");   // The limit should probably be on the higher side. The fewer network DB calls, the better, probably.
				AddOrReplaceParameter(request, "order", "createdAt");
				AddOrReplaceParameter(request, "skip", numProcessed.ToString());

				Logger?.TrackEvent("ParseClient::GetAllResults Request Sent");
				var restResponse = this.Client.Execute(request);
				string responseJson = restResponse.Content;

				ParseResponse<T> response;
				try
				{
					response = JsonConvert.DeserializeObject<Parse.ParseResponse<T>>(responseJson);
				}
				catch (Newtonsoft.Json.JsonReaderException e)
				{
					Logger?.LogWarn("ParseClient::GetAllResults() - JsonReaderException.");
					Logger?.LogVerbose("JsonReaderException: " + e.ToString());
					didExitPrematurely = true;
					return results;
				}

				if (response == null)
				{
					// If the Parse Server is down or restarting, the response might time out after a minute or so
					// and we'll reach this condition.
					Logger?.LogWarn("ParseClient::GetAllResults() - response was null.");
					didExitPrematurely = true;
					return results;
				}

				totalCount = response.Count;
				if (totalCount == 0)
				{
					Console.Out.WriteLine("Query returned no results.");
					break;
				}

				if (response.Results == null)
				{
					Logger?.LogWarn("ParseClient::GetAllResults() - response.Results was null.");
					didExitPrematurely = true;
					return results;
				}

				var currentResultCount = response.Results.Length;				
				if (currentResultCount <= 0)
				{
					break;
				}

				results.AddRange(response.Results);				

				numProcessed += currentResultCount;

				if (numProcessed < totalCount)
				{
					string message = $"GetAllResults Rows Retrieved: {numProcessed} out of {totalCount}.";
					Logger?.LogVerbose(message);
				}
			}
			while (numProcessed < totalCount);

			didExitPrematurely = false;
			return results;
		}

		/// <summary>
		/// Adds the specified parameter with the specified value. If the parameter already exists, the existing value will be overwritten.
		/// </summary>
		/// <param name="request">The object whose Parameters field will be modified</param>
		/// <param name="parameterName">The name of the parameter</param>
		/// <param name="parameterValue">The new value of the parameter</param>
		private void AddOrReplaceParameter(IRestRequest request, string parameterName, string parameterValue)
		{
			if (request.Parameters != null)
			{
				foreach (var param in request.Parameters)
				{
					if (param.Name == parameterName)
					{
						param.Value = parameterValue;
						return;
					}
				}
			}

			// At this point, indicates that no replacements were made while iterating over the params. We'll have to add it in.
			request.AddParameter(parameterName, parameterValue);
		}
	}
}
