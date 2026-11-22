using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.Recording;
using Resgrid.Providers.ApiClient.V4.Models;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Voice;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class DispatchVoiceModeTests
	{
		private static readonly DateTime Now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

		[Test]
		public void PrimeBacklog_MarksOpenCallsSeen_ButLeavesScheduledCallsToBeTonedOut()
		{
			var calls = new[]
			{
				new CallResultData { CallId = "open-dispatched", DispatchedOnUtc = Now.AddMinutes(-5) },
				new CallResultData { CallId = "open-no-dispatch-time" },
				new CallResultData { CallId = "scheduled", DispatchedOnUtc = Now.AddHours(2) },
				new CallResultData { CallId = " " },
				null
			};
			var state = new DispatchToneOutState();

			DispatchVoiceMode.PrimeBacklog(calls, state, Now);

			state.Primed.Should().BeTrue();
			state.Announced.Should().BeEquivalentTo(new[] { "open-dispatched", "open-no-dispatch-time" },
				"a scheduled call primed as seen would never be toned out when it is dispatched");
		}

		[Test]
		public void RetryClock_StandsStillWhilePaused()
		{
			var state = new DispatchToneOutState();
			state.FirstAttemptUtc["c1"] = Now;

			// The channel drops 2 minutes in and stays down for half an hour (a later pause keeps the earlier start).
			state.PauseRetryClock(Now.AddMinutes(2));
			state.PauseRetryClock(Now.AddMinutes(5));
			state.ResumeRetryClock(Now.AddMinutes(32));

			state.FirstAttemptUtc["c1"].Should().Be(Now.AddMinutes(30), "only the 2 minutes the channel was up count toward giving up");
			state.RetryClockPausedSinceUtc.Should().BeNull();

			state.ResumeRetryClock(Now.AddMinutes(40));
			state.FirstAttemptUtc["c1"].Should().Be(Now.AddMinutes(30), "resuming a running clock changes nothing");
		}

		[Test]
		public void IsScheduledForLater_TreatsAnUnspecifiedKindAsUtc()
		{
			var later = DateTime.SpecifyKind(Now.AddMinutes(1), DateTimeKind.Unspecified);
			DispatchVoiceMode.IsScheduledForLater(new CallResultData { DispatchedOnUtc = later }, Now).Should().BeTrue();
			DispatchVoiceMode.IsScheduledForLater(new CallResultData { DispatchedOnUtc = Now.AddMinutes(-1) }, Now).Should().BeFalse();
			DispatchVoiceMode.IsScheduledForLater(new CallResultData(), Now).Should().BeFalse();
		}

		[Test]
		public async Task RunAsync_WithoutTtsServiceUrl_FailsAsAConfigurationError()
		{
			var options = new RelayHostOptions();
			options.Tts.ServiceBaseUrl = "";

			Func<Task> run = () => DispatchVoiceMode.RunAsync(options, logger: null, CancellationToken.None);

			await run.Should().ThrowAsync<RelayConfigurationException>().WithMessage("*ServiceBaseUrl*");
		}

		[Test]
		public async Task RunAsync_HostedWithoutDepartment_FailsAsAConfigurationError()
		{
			var options = new RelayHostOptions();
			options.Tts.ServiceBaseUrl = "http://localhost:1";
			options.DispatchVoice.Hosted = true;
			options.DispatchVoice.DepartmentId = "";
			options.Voice.DepartmentId = "";

			Func<Task> run = () => DispatchVoiceMode.RunAsync(options, logger: null, CancellationToken.None);

			await run.Should().ThrowAsync<RelayConfigurationException>().WithMessage("*DepartmentId*");
		}
	}

	[TestFixture]
	public class RecordModeLogPathTests
	{
		private string _root;

		[SetUp]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), "relay-recordmode-tests-" + Guid.NewGuid().ToString("N"));
		}

		[TearDown]
		public void TearDown()
		{
			try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
		}

		[Test]
		public async Task Jsonl_WithoutLogPath_IsWrittenInsideTheRecordingsFolder()
		{
			var options = new RecorderModeOptions { LocalPath = _root, Log = "jsonl", LogPath = "" };
			var expected = Path.Combine(_root, "transmissions.jsonl");

			RecordMode.ResolveLogPath(options).Should().Be(expected);
			await using (var log = RecordMode.BuildLog(options, logger: null))
				await log.AppendAsync(new TransmissionRecord { Id = "t1" });

			File.Exists(expected).Should().BeTrue("the log must not default next to the (read-only under systemd) app binaries");
		}

		[Test]
		public async Task Sqlite_WithoutLogPath_IsCreatedInsideTheRecordingsFolder()
		{
			var options = new RecorderModeOptions { LocalPath = _root, Log = "sqlite", LogPath = "" };
			var expected = Path.Combine(_root, "transmissions.db");

			RecordMode.ResolveLogPath(options).Should().Be(expected);
			await using (var log = RecordMode.BuildLog(options, logger: null))
				await log.AppendAsync(new TransmissionRecord { Id = "t1" });

			File.Exists(expected).Should().BeTrue();
		}

		[Test]
		public void ExplicitLogPath_Wins()
		{
			var explicitPath = Path.Combine(_root, "elsewhere", "log.jsonl");
			RecordMode.ResolveLogPath(new RecorderModeOptions { LocalPath = _root, LogPath = explicitPath }).Should().Be(explicitPath);
		}

		[Test]
		public void NoLocalPath_FallsBackToTheAppRecordingsFolder()
		{
			RecordMode.ResolveLogPath(new RecorderModeOptions { LocalPath = "", LogPath = "" })
				.Should().Be(Path.Combine(AppContext.BaseDirectory, "recordings", "transmissions.jsonl"));
		}

		[Test]
		public void LogNone_BuildsNoLog()
		{
			RecordMode.BuildLog(new RecorderModeOptions { Log = "none", LocalPath = _root }, logger: null).Should().BeNull();
		}

		[Test]
		public void FallbackDirectory_IsInsideTheRecordingsFolder()
		{
			RecordMode.ResolveFallbackDirectory(new RecorderModeOptions { LocalPath = _root })
				.Should().Be(Path.Combine(_root, "failed-uploads"));
		}
	}

	[TestFixture]
	public class VoiceSessionMonitorTests
	{
		[Test]
		public async Task EnsureConnected_Throws_WhenTheSessionDroppedBeforeTheMonitorAttached()
		{
			var session = new TestVoiceSession { IsConnected = false };
			using var monitor = new VoiceSessionMonitor(session);

			Func<Task> ensure = () => monitor.EnsureConnectedAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

			await ensure.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not connected*");
		}

		[Test]
		public async Task EnsureConnected_Throws_OnADisconnectDuringTheGrace()
		{
			var session = new TestVoiceSession { IsConnected = false };
			using var monitor = new VoiceSessionMonitor(session);
			session.RaiseConnection(false, "ServerShutdown");

			Func<Task> ensure = () => monitor.EnsureConnectedAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

			await ensure.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ServerShutdown*");
		}

		[Test]
		public async Task Reconnecting_DegradesAndCountsAnInterruption_HardDisconnectCompletes()
		{
			var session = new TestVoiceSession();
			var status = new RelayStatus();
			using var monitor = new VoiceSessionMonitor(session, status);
			await monitor.EnsureConnectedAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
			monitor.IsUsable.Should().BeTrue();
			var before = monitor.Interruptions;

			session.RaiseConnection(false, "reconnecting");
			monitor.IsDegraded.Should().BeTrue();
			monitor.IsUsable.Should().BeFalse("audio published while reconnecting is likely lost");
			monitor.IsDisconnected.Should().BeFalse();
			monitor.Interruptions.Should().BeGreaterThan(before);
			status.LiveKit.Should().Be(ConnectionState.Degraded);

			session.RaiseConnection(true, "reconnected");
			monitor.IsUsable.Should().BeTrue();
			status.LiveKit.Should().Be(ConnectionState.Connected);

			session.RaiseConnection(false, "ServerShutdown");
			monitor.IsDisconnected.Should().BeTrue();
			(await monitor.Disconnected).Should().Be("ServerShutdown");
		}

		[Test]
		public void Dispose_Unsubscribes()
		{
			var session = new TestVoiceSession();
			var monitor = new VoiceSessionMonitor(session);
			session.ConnectionChangedSubscribers.Should().Be(1);

			monitor.Dispose();

			session.ConnectionChangedSubscribers.Should().Be(0);
		}
	}
}
