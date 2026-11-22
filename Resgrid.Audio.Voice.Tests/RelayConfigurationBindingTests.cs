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
		public void UnknownProfileKeysFallBackToCustom()
		{
			RadioProfiles.Find("no-such-radio").Should().BeSameAs(RadioProfiles.Custom);
			RadioProfiles.Find(null).Should().BeSameAs(RadioProfiles.Custom);
			RadioProfiles.Find(" RapidRadios ").Key.Should().Be(RadioProfiles.RapidRadiosKey);
		}
	}
}
