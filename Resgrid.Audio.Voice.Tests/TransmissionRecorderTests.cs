using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Recording;

namespace Resgrid.Audio.Voice.Tests
{
	/// <summary>
	/// <see cref="TransmissionRecorder"/> when a store fails or hangs: the audio is kept in the fallback folder, and store
	/// calls are bounded so neither a finalization nor shutdown can wait on a dead store forever.
	/// </summary>
	[TestFixture]
	public class TransmissionRecorderTests
	{
		private string _root;

		[SetUp]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), "relay-recorder-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TearDown]
		public void TearDown()
		{
			try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
		}

		private string FallbackDir => Path.Combine(_root, "failed-uploads");

		private static TransmissionRecorder Recorder(ITransmissionStore[] stores, ITransmissionLog log, string fallbackDirectory) =>
			new TransmissionRecorder(new TestVoiceSession(), new RecorderSettings { ScanIntervalMs = 60_000 }, stores, log, logger: null, fallbackDirectory);

		private static void RecordOneTransmission(TransmissionRecorder recorder)
		{
			var participant = new VoiceParticipant("PA_1", "unit-1", "Engine 1", "standard");
			recorder.Record(new VoiceAudioFrame(participant, "TR_1", TestAudio.Tone(500), DateTime.UtcNow));
		}

		[Test]
		public async Task FailedRemoteStore_SavesTheWavToTheFallbackFolder_AndRecordsWhere()
		{
			var log = new CapturingTransmissionLog();
			var recorder = Recorder(new ITransmissionStore[] { new FailingTransmissionStore() }, log, FallbackDir);
			TransmissionRecord recorded = null;
			recorder.TransmissionRecorded += (_, r) => recorded = r;
			recorder.Start();

			RecordOneTransmission(recorder);
			await recorder.DisposeAsync();

			log.Records.Should().ContainSingle();
			var location = log.Records[0].Locations.Should().ContainSingle().Subject;
			location.Kind.Should().Be("fallback");
			location.Location.Should().StartWith(FallbackDir);
			File.Exists(location.Location).Should().BeTrue();
			new FileInfo(location.Location).Length.Should().Be(location.SizeBytes).And.BeGreaterThan(44);
			recorded.Should().NotBeNull("the audio was kept, so the transmission counts as recorded");
		}

		[Test]
		public async Task HangingStore_IsAbandonedAfterStoreTimeout()
		{
			var log = new CapturingTransmissionLog();
			var recorder = Recorder(new ITransmissionStore[] { new HangingTransmissionStore() }, log, FallbackDir);
			recorder.StoreTimeout = TimeSpan.FromMilliseconds(200);
			recorder.ShutdownGrace = TimeSpan.FromMinutes(5);
			recorder.Start();

			RecordOneTransmission(recorder);
			var stopwatch = Stopwatch.StartNew();
			await recorder.DisposeAsync();

			stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
			log.Records.Should().ContainSingle().Which.Locations.Should().ContainSingle(l => l.Kind == "fallback");
		}

		[Test]
		public async Task HangingStore_DoesNotHangShutdown()
		{
			var log = new CapturingTransmissionLog();
			var recorder = Recorder(new ITransmissionStore[] { new HangingTransmissionStore() }, log, FallbackDir);
			recorder.StoreTimeout = TimeSpan.FromMinutes(5);
			recorder.ShutdownGrace = TimeSpan.FromMilliseconds(200);
			recorder.Start();

			RecordOneTransmission(recorder);
			var stopwatch = Stopwatch.StartNew();
			await recorder.DisposeAsync();

			stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "an upload still running at shutdown is abandoned after the grace period");
			var location = log.Records.Should().ContainSingle().Which.Locations.Should().ContainSingle().Subject;
			location.Kind.Should().Be("fallback");
			File.Exists(location.Location).Should().BeTrue();
		}

		[Test]
		public async Task StoreThatBlocksBeforeReturningItsTask_DoesNotHangShutdown()
		{
			var log = new CapturingTransmissionLog();
			using var store = new BlockingTransmissionStore();
			var recorder = Recorder(new ITransmissionStore[] { store }, log, FallbackDir);
			recorder.StoreTimeout = TimeSpan.FromMinutes(5);
			recorder.ShutdownGrace = TimeSpan.FromMilliseconds(200);
			recorder.Start();

			RecordOneTransmission(recorder);
			var stopwatch = Stopwatch.StartNew();
			// On the pool and bounded, so a regression fails here instead of hanging the run: the store call used to run on
			// the disposing thread itself, where WaitAsync could not bound it.
			var dispose = Task.Run(async () => await recorder.DisposeAsync());
			(await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(dispose, "shutdown must not wait on a blocked store");
			await dispose;

			stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the blocked upload is abandoned after the grace period");
			store.Calls.Should().Be(1);
			var location = log.Records.Should().ContainSingle().Which.Locations.Should().ContainSingle().Subject;
			location.Kind.Should().Be("fallback");
			File.Exists(location.Location).Should().BeTrue();
		}

		[Test]
		public async Task StoreStillBlockedInAnEarlierCall_GetsNoNewCalls_UntilThatCallReturns()
		{
			// The timeout releases a transmission, not the pool worker (and WAV buffer) a blocked store call holds. During an
			// outage each later transmission used to start another such call; it goes straight to the fallback instead.
			var log = new CapturingTransmissionLog();
			using var store = new BlockingTransmissionStore { SucceedOnceReleased = true };
			// Every 1 s frame fills a 1 s segment, so each frame is one transmission, finalized as it arrives.
			var recorder = new TransmissionRecorder(new TestVoiceSession(), new RecorderSettings { ScanIntervalMs = 60_000, MaxSegmentSeconds = 1 },
				new ITransmissionStore[] { store }, log, logger: null, FallbackDir)
			{
				StoreTimeout = TimeSpan.FromMilliseconds(200)
			};
			recorder.Start();
			var participant = new VoiceParticipant("PA_1", "unit-1", "Engine 1", "standard");

			void Transmit(int expectedRecords)
			{
				recorder.Record(new VoiceAudioFrame(participant, "TR_1", TestAudio.Tone(1000), DateTime.UtcNow));
				SpinWait.SpinUntil(() => log.Records.Count >= expectedRecords, TimeSpan.FromSeconds(10)).Should().BeTrue();
			}

			try
			{
				Transmit(1);
				Transmit(2);
				store.Calls.Should().Be(1, "the store was still blocked in the first call, so the second never started one");
				log.Records.Should().OnlyContain(r => r.Locations.Count == 1 && r.Locations[0].Kind == "fallback");

				store.Release();
				// The blocked call returns on its pool worker; give its completion a moment to reopen the store.
				await Task.Delay(500);
				Transmit(3);

				store.Calls.Should().Be(2, "once the blocked call returned, the store is used again");
				log.Records[2].Locations.Should().ContainSingle().Which.Kind.Should().Be("s3");
			}
			finally
			{
				store.Release();
				await recorder.DisposeAsync();
			}
		}

		[Test]
		public async Task ShutdownDuringRollovers_FinishesEveryTransmissionBeforeItReturns_AndStartsNoneAfter()
		{
			var log = new CapturingTransmissionLog();
			// Every 1 s frame fills a 1 s segment, so each one rolls over (and is finalized) from inside the frame callback.
			var recorder = new TransmissionRecorder(new TestVoiceSession(), new RecorderSettings { ScanIntervalMs = 60_000, MaxSegmentSeconds = 1 },
				new ITransmissionStore[] { new MemoryTransmissionStore() }, log, logger: null, FallbackDir);
			recorder.Start();
			var participant = new VoiceParticipant("PA_1", "unit-1", "Engine 1", "standard");
			var second = TestAudio.Tone(1000);

			using var stop = new CancellationTokenSource();
			var fed = 0;
			var feeder = Task.Run(() =>
			{
				while (!stop.IsCancellationRequested)
				{
					recorder.Record(new VoiceAudioFrame(participant, "TR_1", second, DateTime.UtcNow));
					Interlocked.Increment(ref fed);
				}
			});
			SpinWait.SpinUntil(() => Volatile.Read(ref fed) >= 5, TimeSpan.FromSeconds(10)).Should().BeTrue();

			await recorder.DisposeAsync();
			var recordedAtShutdown = log.Records.Count;
			stop.Cancel();
			await feeder;
			recorder.Record(new VoiceAudioFrame(participant, "TR_1", second, DateTime.UtcNow));
			await Task.Delay(300);

			recordedAtShutdown.Should().BeGreaterThan(0);
			log.Records.Should().HaveCount(recordedAtShutdown,
				"a rollover under way at shutdown is finished before DisposeAsync returns, and frames after it start nothing");
		}

		[Test]
		public async Task FailedRemoteStore_WithALocalCopy_WritesNoFallback()
		{
			var log = new CapturingTransmissionLog();
			var localDir = Path.Combine(_root, "local");
			var recorder = Recorder(new ITransmissionStore[] { new LocalFileTransmissionStore(localDir), new FailingTransmissionStore() }, log, FallbackDir);
			recorder.Start();

			RecordOneTransmission(recorder);
			await recorder.DisposeAsync();

			var location = log.Records.Should().ContainSingle().Which.Locations.Should().ContainSingle().Subject;
			location.Kind.Should().Be("local");
			location.Location.Should().StartWith(localDir);
			Directory.Exists(FallbackDir).Should().BeFalse("the local copy already holds the audio");
		}
	}
}
