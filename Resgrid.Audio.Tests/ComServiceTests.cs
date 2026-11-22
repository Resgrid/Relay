using System;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using Serilog;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class ComServiceTests
	{
		private AudioPipelineFakes.Evaluator _evaluator;
		private AudioProcessor _processor;
		private AudioPipelineFakes.CallsApi _calls;
		private AudioPipelineFakes.LookupsApi _lookups;
		private Watcher _watcher;

		[SetUp]
		public void SetUp()
		{
			var storage = new WatcherAudioStorage(null);
			_evaluator = new AudioPipelineFakes.Evaluator();
			_processor = new AudioProcessor(new AudioPipelineFakes.Recorder(storage), _evaluator, storage);
			_calls = new AudioPipelineFakes.CallsApi();
			_lookups = new AudioPipelineFakes.LookupsApi();
			_watcher = new Watcher { Name = "Station 5", Active = true, Code = "station5", Type = (int)DispatchCodeType.Group };
		}

		[TearDown]
		public void TearDown()
		{
			_processor.Stop();
		}

		/// <summary>Pages the watcher and finishes the dispatch at once (Stop sends what is recording).</summary>
		private void Dispatch()
		{
			var config = new Config { AudioLength = 60, Watchers = new List<Watcher> { _watcher } };
			var comService = new ComService(new LoggerConfiguration().CreateLogger(), _processor, new AudioPipelineFakes.ApiClient(), null, _calls, _lookups, "42");
			comService.Init(config);
			_processor.Init(config);

			_evaluator.Trigger(_watcher, new Trigger { Count = 1, Frequency1 = 500, Time1 = 500 }, DateTime.UtcNow);
			_processor.Stop();
		}

		[Test]
		public void A_Transient_Authentication_Failure_During_The_Group_Lookup_Should_Be_Retried()
		{
			// One 503 from the token endpoint while station5 was resolved escaped before the save's retries ran: no call.
			_lookups.FailuresBeforeAnswer = 1;
			_lookups.Failure = () => new ResgridAuthenticationException("The Resgrid token request failed with status code 503", HttpStatusCode.ServiceUnavailable);
			_lookups.GroupLookup = _ => new GroupLookupResult { GroupId = "17" };

			Dispatch();

			_lookups.Attempts.Should().Be(2);
			_calls.SavedCalls.Should().ContainSingle().Which.DispatchList.Should().Be("G:17");
		}

		[Test]
		public void A_Group_Code_Resgrid_Does_Not_Know_Should_Be_Sent_As_Is_Without_Retrying()
		{
			Dispatch();

			_lookups.Attempts.Should().Be(1);
			_calls.SavedCalls.Should().ContainSingle().Which.DispatchList.Should().Be("G:station5");
		}
	}
}
