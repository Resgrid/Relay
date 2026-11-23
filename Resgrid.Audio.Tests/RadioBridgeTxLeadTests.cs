using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core.Radio;
using Resgrid.Audio.Voice.Abstractions;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class RadioBridgeTxLeadTests
	{
		private const int SamplesPerMs = AudioFormat.SampleRate / 1000;

		[Test]
		public async Task TxLead_QueuesSilenceBeforeFirstChannelAudio()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await StartBridge(device, ptt, session, PttKeyingMethod.SerialRts, txLeadMs: 600);

			session.Raise(AudibleFrame());

			ptt.IsKeyed.Should().BeTrue();
			var writes = device.Writes;
			writes.Should().HaveCountGreaterOrEqualTo(2);
			writes[0].Length.Should().Be(600 * SamplesPerMs);
			writes[0].Should().OnlyContain(s => s == 0);
			writes[1].Should().Contain(s => s != 0, "the channel audio follows the lead silence");
		}

		[Test]
		public async Task TxLead_IsQueuedOncePerKeyUp()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await StartBridge(device, ptt, session, PttKeyingMethod.SerialRts, txLeadMs: 400);

			session.Raise(AudibleFrame());
			session.Raise(AudibleFrame());
			session.Raise(AudibleFrame());

			device.Writes.Count(w => w.Length == 400 * SamplesPerMs).Should().Be(1);
		}

		[TestCase(PttKeyingMethod.SerialRts, 0)]
		[TestCase(PttKeyingMethod.Vox, 600)]
		public async Task TxLead_NotAppliedWhenZeroOrVox(PttKeyingMethod method, int txLeadMs)
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await StartBridge(device, ptt, session, method, txLeadMs);

			var frame = AudibleFrame();
			session.Raise(frame);

			device.Writes.Should().HaveCount(1);
			device.Writes[0].Length.Should().Be(frame.Pcm.Length);
		}

		[Test]
		public async Task TxLead_IsCappedAtThreeSeconds()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			await using var bridge = await StartBridge(device, ptt, session, PttKeyingMethod.SerialRts, txLeadMs: 10_000);

			session.Raise(AudibleFrame());

			device.Writes[0].Length.Should().Be(3000 * SamplesPerMs);
		}

		[Test]
		public async Task TxLead_ExtendsTheUnkeyTail()
		{
			var device = new FakeRadioDevice();
			var ptt = new FakePtt();
			var session = new FakeSession();
			// Without the lead the radio would unkey ~100 ms after the last audio; with it, ~700 ms.
			await using var bridge = await StartBridge(device, ptt, session, PttKeyingMethod.SerialRts, txLeadMs: 600,
				txHangMs: 50, txTailMs: 50);

			var stopwatch = Stopwatch.StartNew();
			session.Raise(AudibleFrame());

			await Task.Delay(350);
			ptt.IsKeyed.Should().BeTrue("the queued audio is still playing out behind the lead silence");

			while (ptt.IsKeyed && stopwatch.ElapsedMilliseconds < 5000)
				await Task.Delay(20);

			ptt.IsKeyed.Should().BeFalse();
			stopwatch.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(650);
		}

		private static async Task<RadioBridge> StartBridge(FakeRadioDevice device, FakePtt ptt, FakeSession session,
			PttKeyingMethod method, int txLeadMs, int txHangMs = 300, int txTailMs = 150)
		{
			var settings = new RadioSettings
			{
				Ptt = method,
				TxLeadMs = txLeadMs,
				TxHangMs = txHangMs,
				TxTailMs = txTailMs,
				CourtesyTone = false
			};

			var bridge = new RadioBridge(device, ptt, carrier: null, settings, logger: null);
			await bridge.StartAsync(session, CancellationToken.None);
			return bridge;
		}

		private static VoiceAudioFrame AudibleFrame() => RadioTestAudio.AudibleFrame();
	}
}
