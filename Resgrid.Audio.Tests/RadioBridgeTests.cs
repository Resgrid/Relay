using System;
using System.Diagnostics;
using System.Linq;
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

		/// <summary>
		/// A channel producer running until stopped. Declared (<c>await using</c>) after the bridge, it is stopped and awaited
		/// before the bridge is disposed, also when an assertion failed, so it never outlives its test.
		/// </summary>
		private sealed class Producer : IAsyncDisposable
		{
			private readonly CancellationTokenSource _cts = new CancellationTokenSource();
			private readonly Task _loop;

			public Producer(Func<CancellationToken, Task> loop) => _loop = loop(_cts.Token);

			public async Task StopAsync()
			{
				_cts.Cancel();
				await _loop;
			}

			public async ValueTask DisposeAsync()
			{
				await StopAsync();
				_cts.Dispose();
			}
		}

		private static Producer Talking(FakeSession session) => new Producer(token => Talk(session, token));

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
			await using var talker = Talking(session);

			(await WaitUntil(() => bridge.Transmitting)).Should().BeTrue();
			ptt.IsKeyed.Should().BeFalse();

			ptt.FailWrites = false;
			(await WaitUntil(() => ptt.IsKeyed, 1500)).Should().BeTrue("the bridge re-drives a PTT line a failed write left unkeyed");
			ptt.KeyCalls.Should().BeGreaterThan(1);
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
			await using var talker = Talking(session);

			(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();
			(await WaitUntil(() => !ptt.IsKeyed, 3000)).Should().BeTrue("the transmit time-out drops PTT although the talker keeps talking");
			stopwatch.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(900);

			// Still talking: their audio is ignored, the radio stays unkeyed.
			await Task.Delay(500);
			ptt.IsKeyed.Should().BeFalse();
			ptt.KeyCalls.Should().Be(1);

			await talker.StopAsync();

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
			device.ClearCalls.Should().Be(0, "a normal unkey plays the queue out rather than discarding it");
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
			await using var talker = Talking(session);
			(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();

			// The device took the first audio and then stopped pulling any (no PlaybackStopped): its backlog never shrinks.
			device.BufferedTransmit = TimeSpan.FromMilliseconds(800);
			var stalled = Stopwatch.StartNew();

			(await Task.WhenAny(fault, Task.Delay(6000))).Should().BeSameAs(fault);
			stalled.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(1800, "the backlog has to stay put for ~2 s");
			fault.Result.Reason.Should().Contain("Transmit output stalled");
			ptt.IsKeyed.Should().BeFalse("a dead transmit path must not hold the transmitter keyed");
			bridge.Transmitting.Should().BeFalse();
		}

		[Test]
		public async Task SteadyTransmitBacklog_WhileTheDeviceKeepsPlaying_IsNotAStall()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.MaxTransmitSeconds = 0;
				s.TxHangMs = 300;
			}));

			// A healthy device behind a lead, refilled exactly as fast as it plays: the backlog reads the same on every tick,
			// but the device's playback counter keeps advancing.
			var clock = Stopwatch.StartNew();
			device.BufferedTransmit = TimeSpan.FromMilliseconds(600);
			device.PlayedTransmitSource = () => clock.Elapsed;

			await using var talker = Talking(session);
			(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();

			await Task.Delay(2800);
			bridge.Fault.Should().BeNull("the device is playing; only the queue depth holds steady");
			ptt.IsKeyed.Should().BeTrue();
			ptt.KeyCalls.Should().Be(1);
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

			await using var talker = Talking(session);
			(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();

			await Task.Delay(2800);
			bridge.Fault.Should().BeNull();
			ptt.IsKeyed.Should().BeTrue();
			ptt.KeyCalls.Should().Be(1);
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
			await using var talker = new Producer(token => TalkFor(session, "track-b", 10_000, token));
			(await WaitUntil(() => !ptt.IsKeyed, 5000)).Should().BeTrue("the transmit time-out still cuts off a stuck talker");
			turn.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(1800, "the time-out runs from when this talker took the floor");
			ptt.KeyCalls.Should().Be(1);
		}

		// ---- 18: a transmission cut off by the time-out does not play into the next one ----------------------------------

		[Test]
		public async Task TransmitTimeOut_DiscardsTheCutOffAudio_BeforeTheNextKeyUp()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			// A 2 s lead behind a 1 s time-out: the talker is cut off with all of their speech still queued.
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.MaxTransmitSeconds = 1;
				s.TxLeadMs = 2000;
				s.TxHangMs = 300;
			}));

			await using (var talker = new Producer(token => TalkFor(session, "track-a", 10_000, token)))
			{
				(await WaitUntil(() => ptt.IsKeyed)).Should().BeTrue();
				(await WaitUntil(() => !ptt.IsKeyed, 3000)).Should().BeTrue();
				(await WaitUntil(() => device.ClearCalls > 0)).Should().BeTrue("the cut-off talker's queued audio is discarded at the time-out");
				device.ClearCalls.Should().Be(1);
			}
			var writesAtTimeOut = device.WritesAtLastClear;

			// Quiet past the talker hang: someone else keys up, and their transmission starts clean with its own lead.
			await Task.Delay(700);
			session.Raise(AudibleFrame("track-b"));
			ptt.IsKeyed.Should().BeTrue();
			var writes = device.Writes;
			writes.Count.Should().Be(writesAtTimeOut + 2, "nothing was queued between the discard and the next key-up");
			writes[writesAtTimeOut].Length.Should().Be(2000 * SamplesPerMs);
			writes[writesAtTimeOut].Should().OnlyContain(s => s == 0);
		}

		// ---- 19: a failed key-up holds the audio until the line keys -----------------------------------------------------

		[Test]
		public async Task FailedKey_HoldsTheTransmitAudio_ThenStartsTheLeadWhenTheLineKeys()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt { FailWrites = true };
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.TxLeadMs = 200;
				s.TxHangMs = 300;
			}));
			await using var talker = Talking(session);

			(await WaitUntil(() => bridge.Transmitting)).Should().BeTrue();
			await Task.Delay(100);
			device.Writes.Should().BeEmpty("lead and speech played to a radio that is not keyed are lost");

			ptt.FailWrites = false;
			(await WaitUntil(() => ptt.IsKeyed, 1500)).Should().BeTrue();
			(await WaitUntil(() => device.Writes.Count >= 2)).Should().BeTrue();

			var writes = device.Writes;
			writes[0].Length.Should().Be(200 * SamplesPerMs, "the lead runs from when the line actually keyed");
			writes[0].Should().OnlyContain(s => s == 0);
			writes[1].Should().Contain(s => s != 0, "the held speech follows the lead");
			bridge.Fault.Should().BeNull();
		}

		// ---- 20: a release the PTT line has not confirmed ---------------------------------------------------------------

		[Test]
		public async Task FailedUnkey_KeepsReceiveSuppressed_UntilTheLineReleases()
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

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue();

			ptt.FailWrites = true;
			(await WaitUntil(() => !bridge.Transmitting)).Should().BeTrue();
			ptt.IsKeyed.Should().BeTrue();

			// Well past the 150 ms squelch guard, with the transmitter still on the air: what a loopback interface hears now is
			// our own transmission.
			await Task.Delay(300);
			device.Receive(Tone(20, 8000));
			forwarded.Should().Be(0, "receive stays suppressed while the PTT line has not released");
			bridge.Receiving.Should().BeFalse();

			ptt.FailWrites = false;
			(await WaitUntil(() => !ptt.IsKeyed, 1500)).Should().BeTrue();

			// Past the guard that started when the line actually released: real traffic gets through again.
			await Task.Delay(250);
			device.Receive(Tone(20, 8000));
			forwarded.Should().Be(1);
			bridge.Receiving.Should().BeTrue();
		}

		// ---- 21: receive squelch reset when capture pauses across the guard ---------------------------------------------

		[Test]
		public async Task Squelch_OpenedByOwnTransmission_IsResetOnTheNextFrame_EvenAfterTheGuard()
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
			for (int i = 0; i < 5; i++)
				device.Receive(Tone(20, 10000)); // our own transmission opens the gate (600 ms hang)
			(await WaitUntil(() => !bridge.Transmitting)).Should().BeTrue();

			// No frame at all during the 150 ms guard (capture paused), then residual noise below the open threshold.
			await Task.Delay(250);
			device.Receive(Tone(20, 368)); // about -42 dBFS: between CloseDbfs and OpenDbfs
			forwarded.Should().Be(0, "the hang our own transmission armed is cleared on the first frame after it");
			bridge.Receiving.Should().BeFalse();

			device.Receive(Tone(20, 10000));
			forwarded.Should().Be(1);
		}

		// ---- 22: a fault releases a line that a failed unkey left keyed -------------------------------------------------

		[Test]
		public async Task Fault_ReleasesALineAFailedUnkeyLeftKeyed_AndDiscardsTheQueue()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings());

			session.Raise(AudibleFrame());
			ptt.FailWrites = true;
			(await WaitUntil(() => !bridge.Transmitting)).Should().BeTrue("the bridge has released");
			ptt.IsKeyed.Should().BeTrue("but the line did not follow");

			ptt.FailWrites = false;
			device.Fail("USB audio interface removed");

			ptt.IsKeyed.Should().BeFalse("the fault unkeys the line itself, not only a transmission the bridge still wanted");
			device.ClearCalls.Should().Be(1);
			bridge.Fault.Should().NotBeNull();
		}

		// ---- 23: shutdown --------------------------------------------------------------------------------------------------

		[Test]
		public async Task Dispose_AChannelCallbackStillInFlight_DoesNotKeyTheRadioAgain()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			var bridge = await Start(device, ptt, session, Settings());

			await bridge.DisposeAsync();

			// A frame the transport had already started delivering when the bridge unsubscribed.
			session.RaiseInFlight(AudibleFrame());

			ptt.KeyCalls.Should().Be(0, "nothing keys the radio after the final unkey: no tail timer is left to release it");
			ptt.IsKeyed.Should().BeFalse();
			bridge.Transmitting.Should().BeFalse();
			device.Writes.Should().BeEmpty();
		}

		[Test]
		public async Task Dispose_RetriesAFailedFinalUnkey()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			var bridge = await Start(device, ptt, session, Settings(s => s.TxHangMs = 1000));

			session.Raise(AudibleFrame());
			ptt.IsKeyed.Should().BeTrue();

			ptt.FailWrites = true;
			var disposing = bridge.DisposeAsync().AsTask();
			await Task.Delay(150);
			ptt.IsKeyed.Should().BeTrue("the final unkey failed");
			ptt.FailWrites = false;

			(await Task.WhenAny(disposing, Task.Delay(3000))).Should().BeSameAs(disposing);
			await disposing;
			ptt.IsKeyed.Should().BeFalse("the shutdown keeps re-driving the line for a while; the tail timer is gone");
		}

		// ---- 24: a publish write that never completes ---------------------------------------------------------------------

		[Test]
		public async Task HungPublishWrite_IsCountedAsAFailure_AndPublishingCarriesOn()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			session.Publisher.HangFirst = 1; // never completes, and ignores cancellation
			await using var bridge = await Start(device, ptt, session, Settings());

			for (int i = 0; i < 20; i++)
				device.Receive(Tone(20, 8000));

			// The bridge gives a write 2 s.
			(await WaitUntil(() => session.Publisher.Written == 19, 5000))
				.Should().BeTrue("a write that does not complete is given up on, and the frames behind it are published");
			session.Publisher.Attempts.Should().Be(20);
			bridge.Fault.Should().BeNull("one stalled frame is not a dead channel");
		}

		// ---- 25: a held key-up belongs to its floor turn ------------------------------------------------------------------

		[Test]
		public async Task FailedKey_WhoseTalkerStopsBeforeTheLineKeys_DropsTheHeldAudio_AndNeverKeysForIt()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt { FailWrites = true };
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.TxLeadMs = 200;
				s.TxHangMs = 300;
			}));

			await TalkFor(session, "track-a", 200);
			bridge.Transmitting.Should().BeTrue("the key-up is waiting for the line");

			// Quiet past the 500 ms talker hang: the turn the held key-up was for is over.
			(await WaitUntil(() => !bridge.Transmitting, 2000)).Should().BeTrue("a key-up whose turn has ended is dropped, not retried");
			var keyCalls = ptt.KeyCalls;

			ptt.FailWrites = false;
			await Task.Delay(600);
			ptt.IsKeyed.Should().BeFalse("nothing keys the radio for a turn that has ended");
			ptt.KeyCalls.Should().Be(keyCalls);
			device.Writes.Should().BeEmpty("the expired audio is never played");
			bridge.Fault.Should().BeNull();
		}

		[Test]
		public async Task FailedKey_WhoseTurnEndsDuringASlowRetryWrite_ReleasesTheLine_InsteadOfPlayingTheHeldAudio()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt { FailWrites = true };
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.TxLeadMs = 200;
				s.TxHangMs = 300;
			}));

			await TalkFor(session, "track-a", 200);
			bridge.Transmitting.Should().BeTrue("the key-up is waiting for the line");

			// The next retry (within the talker hang, so the turn is still live) goes through, but the write takes longer than
			// the 500 ms hang: by the time the line is up the turn is over.
			ptt.KeyDelayMs = 900;
			ptt.FailWrites = false;

			(await WaitUntil(() => ptt.KeyCalls > 0 && !bridge.Transmitting && !ptt.IsKeyed, 4000))
				.Should().BeTrue("the line is released once the slow key write returns for an ended turn");
			device.Writes.Should().BeEmpty("the expired turn's held audio is never played");
			bridge.Fault.Should().BeNull();
		}

		[Test]
		public async Task FailedKey_HeldAudioOfAnEndedTurn_IsNotPlayedAheadOfTheNextTalker()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt { FailWrites = true };
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.TxLeadMs = 200;
				s.TxHangMs = 300;
			}));

			// A talks (loud) while the line will not key; B (quieter) is already talking and takes the floor once A's hang runs out.
			await TalkFor(session, "track-a", 200, amplitude: 16000);
			await using (var b = new Producer(token => TalkFor(session, "track-b", 10_000, token, amplitude: 4000)))
			{
				await Task.Delay(900);
				ptt.IsKeyed.Should().BeFalse();
				device.Writes.Should().BeEmpty();

				ptt.FailWrites = false;
				(await WaitUntil(() => ptt.IsKeyed, 1500)).Should().BeTrue("B's turn is on, so the key-up is retried for it");
				(await WaitUntil(() => device.Writes.Count >= 2)).Should().BeTrue();
			}

			var writes = device.Writes;
			writes[0].Length.Should().Be(200 * SamplesPerMs, "the lead runs from when the line actually keyed");
			writes[0].Should().OnlyContain(s => s == 0);
			writes[1].Should().Contain(s => s != 0);
			writes.Skip(1).Should().OnlyContain(w => w.All(s => Math.Abs((int)s) < 8000), "A's expired audio is not played ahead of B");
		}

		[Test]
		public async Task KeyUpsThatAllFail_FaultTheBridge_EvenWhenEveryTurnIsShort()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt { FailWrites = true };
			var session = new FakeSession();
			await using var bridge = await Start(device, ptt, session, Settings(s => s.TxHangMs = 300));
			var fault = NextFault(bridge);

			// 300 ms turns 1.2 s apart: each held key-up is dropped when its turn ends, well inside the 5 s PTT fault time.
			var clock = Stopwatch.StartNew();
			await using var talker = new Producer(async token =>
			{
				while (!token.IsCancellationRequested)
				{
					await TalkFor(session, "track-a", 300, token);
					try { await Task.Delay(900, token); }
					catch (OperationCanceledException) { }
				}
			});

			(await Task.WhenAny(fault, Task.Delay(9000))).Should().BeSameAs(fault, "a line that never keys is still reported");
			fault.Result.Reason.Should().Contain("PTT line not responding");
			clock.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(4500);
		}

		// ---- 26: the squelch guard after a slow unkey write --------------------------------------------------------------

		[Test]
		public async Task SquelchGuard_RunsFromWhenASlowUnkeyWriteReturned()
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
			ptt.IsKeyed.Should().BeTrue();
			ptt.UnkeyDelayMs = 300; // longer than the 150 ms squelch guard

			SpinWait.SpinUntil(() => !bridge.Transmitting, 3000).Should().BeTrue();

			// The loopback still carries the end of our own transmission: it falls inside the guard, which started when the
			// write returned, not before it.
			device.Receive(Tone(20, 10000));
			forwarded.Should().Be(0, "the guard is not used up by the unkey write itself");
			bridge.Receiving.Should().BeFalse();

			await Task.Delay(250);
			device.Receive(Tone(20, 10000));
			forwarded.Should().Be(1, "real traffic gets through once the guard is over");
		}

		// ---- 27: shutdown discards the queued transmit audio --------------------------------------------------------------

		[Test]
		public async Task Dispose_DiscardsTheQueuedTransmitAudio_BeforeClosingThePublisher()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			var bridge = await Start(device, ptt, session, Settings(s =>
			{
				s.Ptt = PttKeyingMethod.Vox;
				s.TxHangMs = 1000;
			}));

			session.Raise(AudibleFrame());
			session.Raise(AudibleFrame());
			device.BufferedTransmit = TimeSpan.FromSeconds(2); // speech still queued: with VOX, the audio itself keys the radio
			var clearsWhenThePublisherCloses = -1;
			session.Publisher.Disposing = () => clearsWhenThePublisherCloses = device.ClearCalls;

			await bridge.DisposeAsync();

			clearsWhenThePublisherCloses.Should().Be(1, "the queue is discarded at the shutdown fence, before the (possibly slow) publisher close");
			device.WritesAtLastClear.Should().Be(device.Writes.Count, "everything queued is discarded");
			device.BufferedTransmit.Should().Be(TimeSpan.Zero);
			ptt.IsKeyed.Should().BeFalse();
		}

		/// <summary>Raises audible frames from one track in real time (10 ms of audio per 10 ms elapsed) for <paramref name="durationMs"/>.</summary>
		private static async Task TalkFor(FakeSession session, string trackSid, int durationMs, CancellationToken token = default, double amplitude = 16000)
		{
			var elapsed = Stopwatch.StartNew();
			long sentMs = 0;
			while (elapsed.ElapsedMilliseconds < durationMs && !token.IsCancellationRequested)
			{
				while (sentMs <= elapsed.ElapsedMilliseconds)
				{
					session.Raise(AudibleFrame(trackSid, amplitude));
					sentMs += 10;
				}
				try { await Task.Delay(5, token); }
				catch (OperationCanceledException) { }
			}
		}
	}
}
