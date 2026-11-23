using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using Serilog;
using Serilog.Core;
using Serilog.Events;

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
		private CapturingSink _log;

		[SetUp]
		public void SetUp()
		{
			var storage = new WatcherAudioStorage(null);
			_evaluator = new AudioPipelineFakes.Evaluator();
			_processor = new AudioProcessor(new AudioPipelineFakes.Recorder(storage), _evaluator, storage);
			_calls = new AudioPipelineFakes.CallsApi();
			_lookups = new AudioPipelineFakes.LookupsApi();
			_watcher = new Watcher { Name = "Station 5", Active = true, Code = "station5", Type = (int)DispatchCodeType.Group };
			_log = new CapturingSink();
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
			var comService = new ComService(new LoggerConfiguration().WriteTo.Sink(_log).CreateLogger(), _processor, new AudioPipelineFakes.ApiClient(), null, _calls, _lookups, "42");
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

		[Test]
		public void A_Group_Lookup_That_Fails_With_A_404_Once_Should_Be_Retried()
		{
			// A routing or gateway hiccup is not "unknown code": once it passes, the group's id is what gets paged.
			_lookups.FailuresBeforeAnswer = 1;
			_lookups.Failure = () => new HttpRequestException("not found", null, HttpStatusCode.NotFound);
			_lookups.GroupLookup = _ => new GroupLookupResult { GroupId = "17" };

			Dispatch();

			_lookups.Attempts.Should().Be(2);
			_calls.SavedCalls.Should().ContainSingle().Which.DispatchList.Should().Be("G:17");
		}

		[Test]
		public void A_Group_Lookup_That_Keeps_Failing_Should_Fail_The_Dispatch_Instead_Of_Sending_The_Raw_Code()
		{
			// station5 is group 17's dispatch code: G:station5 would page nobody, so a lookup outage must not be read as unknown.
			_lookups.FailuresBeforeAnswer = Int32.MaxValue;
			_lookups.Failure = () => new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);
			_lookups.GroupLookup = _ => new GroupLookupResult { GroupId = "17" };

			Dispatch();

			_lookups.Attempts.Should().Be(3, "a 403 is retried like the other recoverable failures");
			_calls.SavedCalls.Should().BeEmpty();
			_log.Events.Should().Contain(x => x.Level == LogEventLevel.Error &&
				x.Exception != null && x.Exception.Message.Contains("station5") && x.Exception.InnerException is HttpRequestException,
				"the lost page is reported with the code whose lookup failed");
		}

		private sealed class CapturingSink : ILogEventSink
		{
			private readonly List<LogEvent> _events = new List<LogEvent>();

			public List<LogEvent> Events
			{
				get
				{
					lock (_events)
						return new List<LogEvent>(_events);
				}
			}

			public void Emit(LogEvent logEvent)
			{
				lock (_events)
					_events.Add(logEvent);
			}
		}
	}
}
