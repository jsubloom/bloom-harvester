using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BloomHarvester;
using BloomHarvester.Parse.Model;
using Newtonsoft.Json;
using NUnit.Framework;
using VSUnitTesting = Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BloomHarvesterTests
{
	[TestFixture]
	public class AlertManagerTests
	{
		[SetUp]
		public void SetupBeforeEachTest()
		{
			AlertManager.Instance.Reset();
		}

		[Test]
		public void NoAlerts_NotSilenced()
		{
			var invoker = new VSUnitTesting.PrivateObject(AlertManager.Instance);
			bool result = (bool)invoker.Invoke("IsSilenced", new Object[] {null});
			Assert.That(result, Is.False);
		}

		[Test]
		public void ReportOneAlert_NotSilenced()
		{
			bool isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced();
			Assert.That(isSilenced, Is.False);
		}

		[Test]
		public void ReportManyAlerts_Silenced()
		{
			bool isSilenced = false;
			for (int i = 0; i < 100; ++i)
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced();
			Assert.That(isSilenced, Is.True);
		}

		[Test]
		public void ReportManyOldAlerts_NotSilenced()
		{
			var alertTimes = new LinkedList<AlertManager.Alert>();
			for (int i = 0; i < 100; ++i)
				alertTimes.AddLast(new AlertManager.Alert { TimeStamp = DateTime.Now.AddDays(-3) });

			var invoker = new VSUnitTesting.PrivateObject(AlertManager.Instance);
			invoker.SetField("_alerts", alertTimes);

			bool isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced();
			Assert.That(isSilenced, Is.False);
		}

		[Test]
		public void ReportNPlus1Alerts_OnlyLastSilenced()
		{
			// N alerts should go through.
			// THe N+1th alert is the first one to be silenced (not the nth)
			bool isSilenced = false;

			for (int i = 0; i < AlertManager.kMaxAlertCount; ++i)
			{
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced();
				Assert.That(isSilenced, Is.False, $"Iteration {i}");
			}

			isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced();
			Assert.That(isSilenced, Is.True);
		}

		[TestCase("New")]
		[TestCase("Updated")]
		[TestCase("Requested")]
		[TestCase("Failed")]
		public void NewBookErrorsGetReportedOncePerBookOrMaxPerUploader(string harvestState)
		{
			// Fill in the maximum number of allowed daily alerts with default data.
			for (var i = 0; i < AlertManager.kMaxAlertCount; ++i)
			{
				var isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced();
				Assert.That(isSilenced, Is.False, $"book {i+1}");
			}

			var model1 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'first book url','title':'First Book','uploader':{{'objectId':'uploader1'}},'harvestState':'{harvestState}'}}");
			var model2 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'second book url','title':'Second Book','uploader':{{'objectId':'uploader1'}},'harvestState':'{harvestState}'}}");
			var model3 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'third book url','title':'Third Book','uploader':{{'objectId':'uploader1'}},'harvestState':'{harvestState}'}}");
			var model4 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'fourth book url','title':'Fourth Book','uploader':{{'objectId':'uploader1'}},'harvestState':'{harvestState}'}}");
			var model5 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'fifth book url','title':'Fifth Book','uploader':{{'objectId':'uploader1'}},'harvestState':'{harvestState}'}}");
			var model6 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'sixth book url','title':'Sixth Book','uploader':{{'objectId':'uploader1'}},'harvestState':'{harvestState}'}}");
			var model7 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'seventh book url','title':'Seventh Book','uploader':{{'objectId':'uploader2'}},'harvestState':'{harvestState}'}}");
			var model8 = JsonConvert.DeserializeObject<BookModel>($"{{'baseUrl':'eighth book url','title':'Eighth Book','uploader':{{'objectId':'uploader2'}},'harvestState':'{harvestState}'}}");

			if (harvestState == "New" || harvestState == "Updated")
			{
				var isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model1);
				Assert.That(isSilenced, Is.False, "first book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model2);
				Assert.That(isSilenced, Is.False, "second book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model3);
				Assert.That(isSilenced, Is.False, "third book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model4);
				Assert.That(isSilenced, Is.False, "fourth book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model5);
				Assert.That(isSilenced, Is.False, "fifth book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model6);
				Assert.That(isSilenced, Is.True, "sixth book: too many by same uploader");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model7);
				Assert.That(isSilenced, Is.False, "seventh book: new uploader");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model8);
				Assert.That(isSilenced, Is.False, "eighth book");

				model7.HarvestState = "Updated";
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model7);
				Assert.That(isSilenced, Is.True, "seventh book updated: already reported once today");
			}
			else
			{
				// neither New nor Updated - silence the report!
				var isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model1);
				Assert.That(isSilenced, Is.True, "first book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model2);
				Assert.That(isSilenced, Is.True, "second book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model3);
				Assert.That(isSilenced, Is.True, "third book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model4);
				Assert.That(isSilenced, Is.True, "fourth book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model5);
				Assert.That(isSilenced, Is.True, "fifth book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model6);
				Assert.That(isSilenced, Is.True, "sixth book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model7);
				Assert.That(isSilenced, Is.True, "seventh book");
				isSilenced = AlertManager.Instance.RecordAlertAndCheckIfSilenced(model8);
				Assert.That(isSilenced, Is.True, "eighth book");
			}
		}
	}
}
