using BloomHarvester.Logger;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BloomHarvester.Parse.Model;

namespace BloomHarvester
{
	/// <summary>
	/// This class keeps track of alerts that are reported and whether alerts should be silenced for being too "noisy" (frequent)
	/// Use the RecordAlertAndCheckIfSilenced() function to do this
	/// </summary>
	public class AlertManager
	{
		internal const int kMaxAlertCount = 5;
		const int kLookbackWindowInHours = 24;

		internal class Alert
		{
			internal DateTime TimeStamp;
			internal string BookUrl;
			internal string HarvestState;
			internal string UploaderId;
		}

		private AlertManager()
		{
			_alerts = new LinkedList<Alert>();
		}

		// Singleton access
		private static AlertManager _instance = null;
		public static AlertManager Instance
		{
			get
			{
				if (_instance == null)
				{
					_instance = new AlertManager();
				}

				return _instance;
			}
		}

		// Other fields/properties
		private LinkedList<Alert> _alerts;	// This list should be maintained in ascending time order
		internal IMonitorLogger Logger { get; set; }

		// Methods

		/// <summary>
		/// Tells the AlertManager to record that an alert is taking place. Also checks if the alert should be silenced or not
		/// </summary>
		/// <returns>Returns true if the current alert should be silenced, false otherwise</returns>
		public bool RecordAlertAndCheckIfSilenced(BookModel bookModel = null)
		{
			var timeStamp = new Alert()
			{
				TimeStamp = DateTime.Now,
				BookUrl = bookModel?.BaseUrl,
				HarvestState = bookModel?.HarvestState,
				UploaderId = bookModel?.Uploader.ObjectId,
			};
			_alerts.AddLast(timeStamp);

			bool isSilenced = this.IsSilenced(bookModel);
			if (isSilenced && Logger != null)
			{
				Logger.TrackEvent("AlertManager: An alert was silenced (too many alerts).");
			}

			return isSilenced;
		}

		/// <summary>
		/// Resets the history of tracked alerts back to empty.
		/// </summary>
		public void Reset()
		{
			_alerts.Clear();
		}

		/// <summary>
		/// Returns whether alerts should currently be silenced
		/// </summary>
		/// <returns>Returns true for silenced, false for not silenced</returns>
		private bool IsSilenced(BookModel bookModel = null)
		{
			// Determine how many alerts have been fired since the start time of the lookback period
			PruneList();

			// Allow reports for "New" or "Updated" books even after exceeding the daily quota for bug
			// reports in general.  But limit to one report per book or MAX reports per uploader.  (Of
			// course, if the uploader persists with one book MAX times, no reports will appear for other
			// books that get uploaded later the same day.)
			// See https://issues.bloomlibrary.org/youtrack/issue/BL-8919.
			if (bookModel?.HarvestState == "New" || bookModel?.HarvestState == "Updated")
			{
				var countForThisBook = _alerts.Count(stamp => stamp?.BookUrl == bookModel.BaseUrl);
				if (countForThisBook > 1)
					return true;		// silence if this book has been reported already today
				var countForUploader = _alerts.Count(stamp => stamp?.UploaderId == bookModel.Uploader.ObjectId);
				return countForUploader > kMaxAlertCount;	// silence for too many errors caused by books from the same uploader.
			}

			// Current model has the same (well, inverted) condition for entering and exiting the Silenced state.
			// Another model could use unrelated conditions for entering vs. exiting the Silenced state
			return _alerts.Count > kMaxAlertCount;
		}

		/// <summary>
		/// // Prunes the list of fired alerts such that it only contains the timestamps within the lookback period.
		/// </summary>
		private void PruneList()
		{
			DateTime startTime = DateTime.Now.Subtract(TimeSpan.FromHours(kLookbackWindowInHours));

			// Precondition: This list must be in sorted order
			while (_alerts.Any() && _alerts.First.Value.TimeStamp < startTime)
			{
				_alerts.RemoveFirst();
			}
		}
	}
}
