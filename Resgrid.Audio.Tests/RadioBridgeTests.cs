using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core.Radio;
using Resgrid.Audio.Voice.Dsp;
using static Resgrid.Audio.Tests.RadioTestAudio;

namespace Resgrid.Audio.Tests
{
	/// <summary>
	/// Fault handling, PTT line robustness, transmit time-out, unkey timing and squelch behavior of <see cref="RadioBridge"/>,
	/// against in-memory device / PTT / session fakes.
	/// </summary>
	[TestFixture]
	public class RadioBridgeTests
	{
		private static RadioSettings Settings(Action<RadioSettings> configure = null)
		{
			var settings = new RadioSettings
			{
				Ptt = PttKeyingMethod.SerialRts,
				TxHangMs = 50,
				TxTailMs = 50,
				CourtesyTone = false,
				Squelch = new SquelchSettings { Mode = SquelchMode.Off }
			};
			configure?.Invoke(settings);
			return settings;
		}

		private static async Task<RadioBridge> Start(FakeRadioDevice device, FakePtt ptt, FakeSession session, RadioSettings settings)
		{
			var bridge = new RadioBridge(device, ptt, carrier: null, settings, logger: null);
			await bridge.StartAsync(session, CancellationToken.None);
			return bridge;
		}

		private static Task<RadioFaultEventArgs> NextFault(RadioBridge bridge)
		{
			var tcs = new TaskCompletionSource<RadioFaultEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
			bridge.Faulted += (_, f) => tcs.TrySetResult(f);
			return tcs.Task;
		}

		/// <summary>Raises audible channel frames from one talker every ~10 ms until cancelled.</summary>
		private static async Task Talk(FakeSession session, CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				session.Raise(AudibleFrame());
				try { await Task.Delay(10, token); }
				catch (OperationCanceledException) { }
			}
		}

		// ---- 1: device faults ---------------------------------------------------------------------------------------------

		[Test]
		public async Task DeviceFault_FaultsTheBridge_AndUnkeysTheRadio()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings());
			var fault = NextFault(bridge);

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue();

			device.Fail("USB audio interface removed");

			(await Task.WhenAny(fault, Task.Delay(3000))).Should().BeSameAs(fault);
			fault.Result.Reason.Should().Contain("USB audio interface removed");
			bridge.Fault.Should().NotBeNull();
			ptt.IsKeyed.Should().BeFalse("a dead transmit path must not hold the transmitter keyed");
			bridge.Transmitting.Should().BeFalse();

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeFalse("a faulted bridge no longer keys the radio");
		}

		// ---- 2: publish pump --------------------------------------------------------------------------------------------

		[Test]
		public async Task PublishFailure_DropsTheFrame_AndKeepsPublishing()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			session.Publisher.FailFirst = 5;
			await using var bridge = await Start(device, ptt, session, Settings());

			for (int i = 0; i < 20; i++)
				device.Receive(Tone(20, 8000));

			(await WaitUntil(() => session.Publisher.Written == 15)).Should().BeTrue("the pump carries on past failed frames");
			session.Publisher.Attempts.Should().Be(20);
			bridge.Fault.Should().BeNull("a handful of failed frames is not a dead channel");
		}

		[Test]
		public async Task PublishFailingPersistently_FaultsTheBridge()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			session.Publisher.AlwaysFail = true;
			await using var bridge = await Start(device, ptt, session, Settings());
			var fault = NextFault(bridge);

			// More than the bridge's consecutive-failure limit (100).
			for (int i = 0; i < 150; i++)
				device.Receive(Tone(20, 8000));

			(await Task.WhenAny(fault, Task.Delay(3000))).Should().BeSameAs(fault);
			fault.Result.Reason.Should().Contain("Publishing radio audio");
		}

		// ---- 7: PTT line failures and the transmit time-out --------------------------------------------------------------

		[Test]
		public async Task FailedKey_IsRetried_UntilTheLineKeys()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt { FailWrites = true };
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s => s.TxHangMs = 300));
			using var cts = new CancellationTokenSource();
			var talking = Talk(session, cts.Token);

			(await WaitUntil(() => bridge.Transmitting)).Should().BeTrue();
			ptt.IsKeyed.Should().BeFalse();

			ptt.FailWrites = false;
			(await WaitUntil(() => ptt.IsKeyed, 1500)).Should().BeTrue("the bridge re-drives a PTT line a failed write left unkeyed");
			ptt.KeyCalls.Should().BeGreaterThan(1);

			cts.Cancel();
			await talking;
		}

		[Test]
		public async Task FailedUnkey_IsRetried_UntilTheLineReleases()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings());

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue();

			ptt.FailWrites = true;
			(await WaitUntil(() => !bridge.Transmitting)).Should().BeTrue("the tail runs out and the bridge releases");
			ptt.IsKeyed.Should().BeTrue("the unkey write failed, so the transmitter is still on the air");

			var unkeysSoFar = ptt.UnkeyCalls;
			await Task.Delay(600);
			ptt.UnkeyCalls.Should().BeGreaterThan(unkeysSoFar, "a stuck PTT line must keep being released");

			ptt.FailWrites = false;
			(await WaitUntil(() => !ptt.IsKeyed, 1500)).Should().BeTrue();
			bridge.Fault.Should().BeNull();
		}

		[Test]
		public async Task PttLineThatNeverResponds_FaultsTheBridge()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings());
			var fault = NextFault(bridge);

			session.Raise(AudibleFrame());
			ptt.FailWrites = true; // the unkey will never go through

			(await Task.WhenAny(fault, Task.Delay(8000))).Should().BeSameAs(fault);
			fault.Result.Reason.Should().Contain("PTT line not responding");
		}

		[Test]
		public async Task MaxTransmitSeconds_UnkeysAStuckTalker_AndIgnoresThemUntilTheyGoQuiet()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			// TxHangMs 300 (not the fast-test 50): a scheduling hiccup in the talk loop on a loaded machine must not unkey and
			// re-key the radio, which would break the single-key-up assertion below.
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.MaxTransmitSeconds = 1;
				s.TxHangMs = 300;
			}));

			var stopwatch = Stopwatch.StartNew();
			using var cts = new CancellationTokenSource();
			var talking = Talk(session, cts.Token);

			(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();
			(await WaitUntil(() => !ptt.IsKeyed, 3000)).Should().BeTrue("the transmit time-out drops PTT although the talker keeps talking");
			stopwatch.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(900);

			// Still talking: their audio is ignored, the radio stays unkeyed.
			await Task.Delay(500);
			ptt.IsKeyed.Should().BeFalse();
			ptt.KeyCalls.Should().Be(1);

			cts.Cancel();
			await talking;

			// Quiet for longer than the talker hang (500 ms): the next transmission keys again.
			await Task.Delay(700);
			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue();
			ptt.KeyCalls.Should().Be(2);
		}

		// ---- 8: unkey waits for the device's backlog and output latency ----------------------------------------------------

		[Test]
		public async Task Unkey_WaitsForTheDeviceBuffer_ThenOutputLatencyAndTail()
		{
			var device = new FakeRadioDevice { OutputLatency = TimeSpan.FromMilliseconds(100) };
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings());

			session.Raise(AudibleFrame());
			device.BufferedTransmit = TimeSpan.FromSeconds(1);

			await Task.Delay(400);
			ptt.IsKeyed.Should().BeTrue("the device still has audio queued");

			var drained = Stopwatch.StartNew();
			device.BufferedTransmit = TimeSpan.Zero;
			(await WaitUntil(() => !ptt.IsKeyed)).Should().BeTrue();
			drained.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(140, "output latency (100 ms) + tail (50 ms) play out after the queue drains");
		}

		// ---- 9: key-up records the new transmission before the line is driven ----------------------------------------------

		[Test]
		public async Task SlowKeyUp_IsNotUnkeyedOrBeepedByATimerTickInTheGap()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt { KeyDelayMs = 200 };
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.TxHangMs = 300;
				s.TxTailMs = 150;
				s.CourtesyTone = true;
			}));

			var frame = AudibleFrame();
			session.Raise(frame);

			ptt.IsKeyed.Should().BeTrue("a tick during the slow key-up must not see the previous (expired) idle time");
			ptt.UnkeyCalls.Should().Be(0);
			device.Writes.Should().HaveCount(1, "no courtesy beep at the start of the transmission");
			device.Writes[0].Length.Should().Be(frame.Pcm.Length);
		}

		// ---- 11: receive squelch after our own transmission --------------------------------------------------------------

		[Test]
		public async Task Squelch_OpenedByOwnTransmission_IsResetOnUnkey()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.AntiLoop = true;
				s.Squelch = new SquelchSettings { Mode = SquelchMode.Vox, OpenDbfs = -38, CloseDbfs = -45, HangMs = 600 };
			}));
			var forwarded = 0;
			bridge.RadioAudioForwarded += (_, __) => Interlocked.Increment(ref forwarded);

			session.Raise(AudibleFrame());
			bridge.Transmitting.Should().BeTrue();

			// A loopback interface hears our own transmission: it opens the gate, but is not forwarded while transmitting.
			for (int i = 0; i < 5; i++)
				device.Receive(Tone(20, 10000));
			forwarded.Should().Be(0);

			(await WaitUntil(() => !bridge.Transmitting)).Should().BeTrue();

			// Residual noise right after unkeying (below the open threshold) must not ride the hang our own TX armed.
			var quiet = Tone(20, 368); // about -42 dBFS: between CloseDbfs and OpenDbfs
			device.Receive(quiet);
			await Task.Delay(250);
			device.Receive(quiet);
			forwarded.Should().Be(0);
			bridge.Receiving.Should().BeFalse();

			// Real traffic still opens it.
			device.Receive(Tone(20, 10000));
			forwarded.Should().Be(1);
		}

		// ---- 14: dispose clears the live state ----------------------------------------------------------------------------

		[Test]
		public async Task Dispose_ClearsTransmittingAndReceiving_AndRaisesTxRxChanged()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			var bridge = await Start(device, ptt, session, Settings(s => s.AntiLoop = false));

			session.Raise(AudibleFrame());
			device.Receive(Tone(20, 8000));
			bridge.Transmitting.Should().BeTrue();
			bridge.Receiving.Should().BeTrue();

			var changes = 0;
			bridge.TxRxChanged += (_, __) => Interlocked.Increment(ref changes);

			await bridge.DisposeAsync();

			bridge.Transmitting.Should().BeFalse();
			bridge.Receiving.Should().BeFalse();
			changes.Should().BeGreaterThan(0);
			ptt.IsKeyed.Should().BeFalse();
		}

		// ---- 15: squelch Off (the radio's own hardware squelch gates the audio) --------------------------------------------

		[Test]
		public async Task SquelchOff_SilentReceiveAudio_IsNotReceiving_AndDoesNotBlockTransmit()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s => s.AntiLoop = true));
			var forwarded = 0;
			bridge.RadioAudioForwarded += (_, __) => Interlocked.Increment(ref forwarded);

			// A second of a closed hardware squelch: digital silence and a little hiss below OpenDbfs (-38).
			var silence = new short[20 * SamplesPerMs];
			var hiss = Tone(20, 368); // about -42 dBFS
			for (int i = 0; i < 50; i++)
				device.Receive(i % 2 == 0 ? silence : hiss);

			bridge.Receiving.Should().BeFalse("a closed hardware squelch is not traffic");
			forwarded.Should().Be(0);

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue("half-duplex arbitration must not hold the transmitter off on silence");
			bridge.Transmitting.Should().BeTrue();

			await Task.Delay(100);
			session.Publisher.Attempts.Should().Be(0, "silence is not published to the channel");
		}

		[Test]
		public async Task SquelchOff_AudibleReceiveAudio_IsReceived_HeldOverPauses_ThenReleased()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.AntiLoop = true;
				s.Squelch = new SquelchSettings { Mode = SquelchMode.Off, OpenDbfs = -38, HangMs = 100 };
			}));
			var forwarded = 0;
			bridge.RadioAudioForwarded += (_, __) => Interlocked.Increment(ref forwarded);
			var silence = new short[20 * SamplesPerMs];

			device.Receive(Tone(20, 8000));
			bridge.Receiving.Should().BeTrue();
			forwarded.Should().Be(1);

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeFalse("half-duplex: the radio is receiving real traffic");

			// A 40 ms pause in the speech is inside the 100 ms hang: still receiving, and passed through.
			device.Receive(silence);
			device.Receive(silence);
			bridge.Receiving.Should().BeTrue();
			forwarded.Should().Be(3);

			// Past the hang the squelch closes, and the silence after it is no longer published.
			for (int i = 0; i < 5; i++)
				device.Receive(silence);
			bridge.Receiving.Should().BeFalse();
			var forwardedAtClose = forwarded;
			forwardedAtClose.Should().BeInRange(4, 6, "only the frames inside the 100 ms hang are passed");
			for (int i = 0; i < 20; i++)
				device.Receive(silence);
			forwarded.Should().Be(forwardedAtClose);

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue("the receive has ended, so channel audio keys the radio");

			(await WaitUntil(() => session.Publisher.Written == forwardedAtClose)).Should().BeTrue();
			await Task.Delay(100);
			session.Publisher.Attempts.Should().Be(forwardedAtClose);
		}

		// ---- 16: an output device that stops playing without reporting it -------------------------------------------------

		[Test]
		public async Task Unkey_DoesNotWaitOnADeviceBacklogFarBeyondWhatWasQueued()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			// No transmit time-out to fall back on: only the bounded drain wait can unkey.
			await using var bridge = await Start(device, ptt, session, Settings(s => s.MaxTransmitSeconds = 0));

			// The device claims a 30 s backlog after 10 ms of audio, shrinking at a crawl: still moving (so not a stall), but it
			// would hold the radio keyed for half a minute.
			var clock = Stopwatch.StartNew();
			device.BufferedTransmitSource = () => TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(clock.Elapsed.Ticks / 100);

			var keyed = Stopwatch.StartNew();
			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue();

			(await WaitUntil(() => !ptt.IsKeyed, 4000)).Should().BeTrue("the unkey waits at most ~1 s past what the bridge itself queued");
			keyed.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(900, "the device's backlog is still waited on, up to that bound");
			bridge.Fault.Should().BeNull("the backlog keeps shrinking, so the output is not stalled");
		}

		[Test]
		public async Task StalledTransmitOutput_FaultsTheBridge_AndUnkeys()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.MaxTransmitSeconds = 0;
				s.TxHangMs = 300;
			}));
			var fault = NextFault(bridge);

			// A talker who keeps talking keeps the radio keyed: only the stall detection can end this transmission.
			using var cts = new CancellationTokenSource();
			var talking = Talk(session, cts.Token);
			(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();

			// The device took the first audio and then stopped pulling any (no PlaybackStopped): its backlog never shrinks.
			device.BufferedTransmit = TimeSpan.FromMilliseconds(800);
			var stalled = Stopwatch.StartNew();

			(await Task.WhenAny(fault, Task.Delay(6000))).Should().BeSameAs(fault);
			stalled.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(1800, "the backlog has to stay put for ~2 s");
			fault.Result.Reason.Should().Contain("Transmit output stalled");
			ptt.IsKeyed.Should().BeFalse("a dead transmit path must not hold the transmitter keyed");
			bridge.Transmitting.Should().BeFalse();

			cts.Cancel();
			await talking;
		}

		[Test]
		public async Task ShrinkingTransmitBacklog_RefilledInBursts_IsNotAStall()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.MaxTransmitSeconds = 0;
				s.TxHangMs = 300;
			}));

			// A healthy device: plays its backlog out continuously, topped up every 400 ms.
			var clock = Stopwatch.StartNew();
			device.BufferedTransmitSource = () =>
				TimeSpan.FromMilliseconds(1000) - TimeSpan.FromTicks(clock.Elapsed.Ticks % TimeSpan.FromMilliseconds(400).Ticks);

			using var cts = new CancellationTokenSource();
			var talking = Talk(session, cts.Token);
			(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();

			await Task.Delay(2800);
			bridge.Fault.Should().BeNull();
			ptt.IsKeyed.Should().BeTrue();
			ptt.KeyCalls.Should().Be(1);

			cts.Cancel();
			await talking;
		}

		// ---- 17: the transmit time-out is per talker ---------------------------------------------------------------------

		[Test]
		public async Task MaxTransmitSeconds_IsTimedFromWhenTheTalkerTookTheFloor_NotFromKeyUp()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			// A PoC lead delays the audio by 3 s, so the radio stays keyed across the gaps between quick turns.
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.MaxTransmitSeconds = 2;
				s.TxLeadMs = 3000;
				s.TxHangMs = 300;
				s.TxTailMs = 150;
			}));

			// Back-and-forth: each turn is 500 ms, each gap 700 ms (past the 500 ms talker hang, so the floor changes hands).
			await TalkFor(session, "track-a", 500);
			ptt.IsKeyed.Should().BeTrue();
			await Task.Delay(700);
			await TalkFor(session, "track-b", 500);
			await Task.Delay(700);
			await TalkFor(session, "track-a", 500);
			await Task.Delay(700);

			// Keyed for ~3.6 s, past the 2 s limit as a whole, but nobody has held the floor for 2 s.
			ptt.IsKeyed.Should().BeTrue("no single talker has reached the transmit time-out");
			ptt.UnkeyCalls.Should().Be(0);
			ptt.KeyCalls.Should().Be(1);

			// A talker who does hold the floor past the limit is still cut off, timed from when they took it.
			var turn = Stopwatch.StartNew();
			using var cts = new CancellationTokenSource();
			var talking = TalkFor(session, "track-b", 10_000, cts.Token);
			(await WaitUntil(() => !ptt.IsKeyed, 5000)).Should().BeTrue("the transmit time-out still cuts off a stuck talker");
			turn.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(1800, "the time-out runs from when this talker took the floor");
			ptt.KeyCalls.Should().Be(1);

			cts.Cancel();
			await talking;
		}

		/// <summary>Raises audible frames from one track in real time (10 ms of audio per 10 ms elapsed) for <paramref name="durationMs"/>.</summary>
		private static async Task TalkFor(FakeSession session, string trackSid, int durationMs, CancellationToken token = default)
		{
			var elapsed = Stopwatch.StartNew();
			long sentMs = 0;
			while (elapsed.ElapsedMilliseconds < durationMs && !token.IsCancellationRequested)
			{
				while (sentMs <= elapsed.ElapsedMilliseconds)
				{
					session.Raise(AudibleFrame(trackSid));
					sentMs += 10;
				}
				try { await Task.Delay(5, token); }
				catch (OperationCanceledException) { }
			}
		}
	}
}
