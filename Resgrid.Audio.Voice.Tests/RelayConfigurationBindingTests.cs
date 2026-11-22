using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class RelayConfigurationBindingTests
	{
		private static IConfiguration Config(params (string Key, string Value)[] values) =>
			new ConfigurationBuilder()
				.AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)))
				.Build();

		[Test]
		public void ConfiguredDomainsReplaceTheDefaults()
		{
			var options = RelayConfiguration.Bind(Config(("Smtp:DepartmentAddressDomains:0", "pager.county.gov")));

			options.Smtp.DepartmentAddressDomains.Should().Equal("pager.county.gov");
		}

		[Test]
		public void UnconfiguredDomainTypesKeepTheirDefault()
		{
			var options = RelayConfiguration.Bind(Config(("Smtp:DepartmentAddressDomains:0", "pager.county.gov")));

			options.Smtp.GroupAddressDomains.Should().Equal("groups.resgrid.com");
			options.Smtp.GroupMessageAddressDomains.Should().Equal("gm.resgrid.com");
			options.Smtp.ListAddressDomains.Should().Equal("lists.resgrid.com");
		}

		[Test]
		public void SaveAndReloadDoesNotGrowTheDomainLists()
		{
			// The desktop app serializes the bound options and binds the file again on every save.
			var first = RelayConfiguration.Bind(Config(("Smtp:GroupAddressDomains:0", "groups.example.org")));

			var reloaded = first;
			for (var i = 0; i < 3; i++)
			{
				var json = JsonSerializer.Serialize(reloaded);
				var builder = new ConfigurationBuilder()
					.AddJsonStream(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
				reloaded = RelayConfiguration.Bind(builder.Build());
			}

			reloaded.Smtp.GroupAddressDomains.Should().Equal("groups.example.org");
			reloaded.Smtp.DepartmentAddressDomains.Should().Equal("dispatch.resgrid.com");
		}

		[Test]
		public void AnEmptyListKeepsTheDefaultAsBefore()
		{
			// The shipped appsettings.json has [] for every list; deployments that never set domains served the defaults.
			var json = "{ \"Smtp\": { \"DepartmentAddressDomains\": [], \"GroupAddressDomains\": [ \"groups.example.org\" ] } }";
			var builder = new ConfigurationBuilder()
				.AddJsonStream(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

			var options = RelayConfiguration.Bind(builder.Build());

			options.Smtp.DepartmentAddressDomains.Should().Equal("dispatch.resgrid.com");
			options.Smtp.GroupAddressDomains.Should().Equal("groups.example.org");
			options.Smtp.ListAddressDomains.Should().Equal("lists.resgrid.com");
		}

		[Test]
		public void AnUnreadableValueIsAConfigurationError()
		{
			var act = () => RelayConfiguration.Bind(Config(("Smtp:Port", "not-a-number")));

			act.Should().Throw<RelayConfigurationException>().WithMessage("*Invalid relay setting*");
		}

		[Test]
		public void WithEnvironmentLayersEnvironmentValuesOverTheSavedModelWithoutChangingIt()
		{
			const string name = "RESGRID__RELAY__Smtp__ServerName";
			var previous = System.Environment.GetEnvironmentVariable(name);
			try
			{
				System.Environment.SetEnvironmentVariable(name, "from-environment");
				var saved = new RelayHostOptions();
				saved.Smtp.ServerName = "from-file";
				saved.Smtp.Port = 2626;

				var effective = RelayConfiguration.WithEnvironment(saved);

				effective.Smtp.ServerName.Should().Be("from-environment");
				effective.Smtp.Port.Should().Be(2626);
				saved.Smtp.ServerName.Should().Be("from-file", "the model the UI saves must never pick up environment values");
			}
			finally
			{
				System.Environment.SetEnvironmentVariable(name, previous);
			}
		}

		[Test]
		public void TheUserFileHoldsOnlyWhatDiffersFromTheDefaults()
		{
			var options = new RelayHostOptions();
			options.Radio.TxLeadMs = 650;
			options.Smtp.GroupAddressDomains = new[] { "groups.example.org" };

			var json = System.Text.Json.Nodes.JsonNode.Parse(RelayConfiguration.BuildUserFileJson(options)).AsObject();

			json.Select(p => p.Key).Should().BeEquivalentTo("Radio", "Smtp");
			json["Radio"].AsObject().Select(p => p.Key).Should().Equal("TxLeadMs");
			json["Radio"]["TxLeadMs"].GetValue<int>().Should().Be(650);
			json["Smtp"].AsObject().Select(p => p.Key).Should().Equal("GroupAddressDomains");

			// And it loads back to the same effective settings.
			var reloaded = RelayConfiguration.Bind(new ConfigurationBuilder()
				.AddJsonStream(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())))
				.Build());
			reloaded.Radio.TxLeadMs.Should().Be(650);
			reloaded.Smtp.GroupAddressDomains.Should().Equal("groups.example.org");
			reloaded.Smtp.DepartmentAddressDomains.Should().Equal("dispatch.resgrid.com");
		}

		private static IConfigurationBuilder AddJson(IConfigurationBuilder builder, string json) =>
			builder.AddJsonStream(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

		[Test]
		public void RemovingADomainInAHigherLayerReplacesTheLowerLayersList()
		{
			// appsettings.json lists two domains; the user file (written by the desktop app) keeps only the first. Layers merge
			// arrays by index, so the second used to come back from appsettings.json.
			var builder = new ConfigurationBuilder();
			AddJson(builder, "{ \"Smtp\": { \"GroupMessageAddressDomains\": [ \"gm.one.org\", \"gm.two.org\" ] } }");
			AddJson(builder, "{ \"Smtp\": { \"GroupMessageAddressDomains\": [ \"gm.one.org\" ] } }");

			var options = RelayConfiguration.Bind(builder.Build());

			options.Smtp.GroupMessageAddressDomains.Should().Equal("gm.one.org");
		}

		[Test]
		public void AddingADomainOverAnEmptyLowerList()
		{
			var builder = new ConfigurationBuilder();
			AddJson(builder, "{ \"Smtp\": { \"ListAddressDomains\": [] } }");
			AddJson(builder, "{ \"Smtp\": { \"ListAddressDomains\": [ \"lists.example.org\" ] } }");

			RelayConfiguration.Bind(builder.Build()).Smtp.ListAddressDomains.Should().Equal("lists.example.org");
		}

		[Test]
		public void AnEmptyHigherLayerListFallsThroughToTheLowerLayer()
		{
			var builder = new ConfigurationBuilder();
			AddJson(builder, "{ \"Smtp\": { \"GroupAddressDomains\": [ \"groups.one.org\", \"groups.two.org\" ] } }");
			AddJson(builder, "{ \"Smtp\": { \"GroupAddressDomains\": [] } }");

			RelayConfiguration.Bind(builder.Build()).Smtp.GroupAddressDomains.Should().Equal("groups.one.org", "groups.two.org");
		}

		[Test]
		public void AnEnvironmentDomainListReplacesTheSavedList()
		{
			const string name = "RESGRID__RELAY__Smtp__DepartmentAddressDomains__0";
			var previous = System.Environment.GetEnvironmentVariable(name);
			try
			{
				System.Environment.SetEnvironmentVariable(name, "env.example.org");
				var saved = new RelayHostOptions();
				saved.Smtp.DepartmentAddressDomains = new[] { "one.example.org", "two.example.org" };

				RelayConfiguration.WithEnvironment(saved).Smtp.DepartmentAddressDomains.Should().Equal("env.example.org");
			}
			finally
			{
				System.Environment.SetEnvironmentVariable(name, previous);
			}
		}

		[Test]
		public void ClearingAValueSetInAppSettingsIsSavedAsAnOverride()
		{
			var appSettings = new RelayHostOptions();
			appSettings.Smtp.DefaultDepartmentId = "dept123";
			var edited = new RelayHostOptions();
			edited.Smtp.DefaultDepartmentId = null;
			edited.Smtp.RedisCache = null;

			var json = System.Text.Json.Nodes.JsonNode.Parse(RelayConfiguration.BuildUserFileJson(edited, appSettings)).AsObject();

			json["Smtp"].AsObject().ContainsKey("DefaultDepartmentId").Should().BeTrue("an explicit null must override appsettings.json");
			json["Smtp"]["DefaultDepartmentId"].Should().BeNull();
			json["Smtp"].AsObject().ContainsKey("RedisCache").Should().BeFalse("a null section is not written over the defaults");

			var builder = new ConfigurationBuilder();
			AddJson(builder, JsonSerializer.Serialize(appSettings));
			AddJson(builder, json.ToJsonString());
			RelayConfiguration.Bind(builder.Build()).Smtp.DefaultDepartmentId.Should().BeNullOrEmpty();
		}

		[Test]
		public void UnchangedValuesAreStillLeftOut()
		{
			var appSettings = new RelayHostOptions();
			appSettings.Smtp.DefaultDepartmentId = "dept123";
			var edited = new RelayHostOptions();
			edited.Smtp.DefaultDepartmentId = "dept123";

			RelayConfiguration.BuildUserFileJson(edited, appSettings).Trim().Should().Be("{}");
		}

		[Test]
		public void WithEnvironmentResolvesARelativeTokenCachePathAgainstTheBaseDirectory()
		{
			const string name = "RESGRID__RELAY__Resgrid__TokenCachePath";
			var previous = System.Environment.GetEnvironmentVariable(name);
			try
			{
				System.Environment.SetEnvironmentVariable(name, "cache/token.json");

				var effective = RelayConfiguration.WithEnvironment(new RelayHostOptions());

				effective.Resgrid.TokenCachePath.Should().Be(
					System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "cache/token.json")));
			}
			finally
			{
				System.Environment.SetEnvironmentVariable(name, previous);
			}
		}

		[Test]
		public void EnvironmentValueReadsTheEnvironmentLayer()
		{
			const string name = "RESGRID__RELAY__Resgrid__ClientId";
			var previous = System.Environment.GetEnvironmentVariable(name);
			try
			{
				System.Environment.SetEnvironmentVariable(name, "client-from-env");
				RelayConfiguration.EnvironmentValue("Resgrid:ClientId").Should().Be("client-from-env");

				System.Environment.SetEnvironmentVariable(name, null);
				RelayConfiguration.EnvironmentValue("Resgrid:ClientId").Should().BeNull();
			}
			finally
			{
				System.Environment.SetEnvironmentVariable(name, previous);
			}
		}

		[Test]
		public void TheUserFileParserAcceptsWhatTheConfigurationLoaderAccepts()
		{
			var root = RelayConfiguration.ParseUserFile("{\n  // tuned on site\n  \"Radio\": { \"TxLeadMs\": 650, },\n}");

			root["Radio"]["TxLeadMs"].GetValue<int>().Should().Be(650);
		}

		[Test]
		public void RadioProfileIsBound()
		{
			var options = RelayConfiguration.Bind(Config(("Radio:Profile", "digatalkplus"), ("Radio:TxLeadMs", "650")));

			options.Radio.Profile.Should().Be("digatalkplus");
			options.Radio.TxLeadMs.Should().Be(650);
		}
	}

	[TestFixture]
	public class RadioProfileTests
	{
		[Test]
		public void CustomProfileLeavesSettingsUnchanged()
		{
			var radio = new RadioModeOptions { TxLeadMs = 123, TxHangMs = 456, CourtesyTone = true };

			RadioProfiles.Custom.ApplyTo(radio);

			radio.TxLeadMs.Should().Be(123);
			radio.TxHangMs.Should().Be(456);
			radio.Profile.Should().Be(RadioProfiles.CustomKey);
		}

		[TestCase(RadioProfiles.RapidRadiosKey)]
		[TestCase(RadioProfiles.DigaTalkPlusKey)]
		[TestCase(RadioProfiles.GenericPocKey)]
		public void CellularProfilesSetALeadAndTurnTheCourtesyToneOff(string key)
		{
			var radio = new RadioModeOptions { PttMethod = "Cm108", SerialPort = "COM7", InputDevice = 2, OutputDevice = 3 };

			var profile = RadioProfiles.Find(key);
			profile.ApplyTo(radio);

			profile.IsPushToTalkOverCellular.Should().BeTrue();
			radio.Profile.Should().Be(key);
			radio.TxLeadMs.Should().BeInRange(500, 800);
			radio.CourtesyTone.Should().BeFalse();
			radio.Squelch.Mode.Should().Be(SquelchMode.Vox);
			radio.Emergency.DetectMdc1200.Should().BeFalse();

			// Wiring is the cable's business, not the radio profile's.
			radio.PttMethod.Should().Be("Cm108");
			radio.SerialPort.Should().Be("COM7");
			radio.InputDevice.Should().Be(2);
			radio.OutputDevice.Should().Be(3);
		}

		[Test]
		public void RapidRadiosNotesGiveTheAiocUsbIds()
		{
			// The AIOC is not a C-Media chip: with the default VID 0x0D8C the CM108 PTT controller does not find it.
			var notes = RadioProfiles.Find(RadioProfiles.RapidRadiosKey).SetupNotes;

			notes.Should().Contain("0x1209").And.Contain("0x7388").And.Contain("Cm108VendorId").And.Contain("Cm108ProductId");
		}

		[Test]
		public void UnknownProfileKeysFallBackToCustom()
		{
			RadioProfiles.Find("no-such-radio").Should().BeSameAs(RadioProfiles.Custom);
			RadioProfiles.Find(null).Should().BeSameAs(RadioProfiles.Custom);
			RadioProfiles.Find(" RapidRadios ").Key.Should().Be(RadioProfiles.RapidRadiosKey);
		}
	}
}
