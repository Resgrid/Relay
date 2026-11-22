using System;
using System.Diagnostics;
using System.IO;
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
