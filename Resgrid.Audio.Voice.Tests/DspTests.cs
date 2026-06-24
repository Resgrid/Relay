using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Audio.Voice.ToneOut;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class GoertzelTests
	{
		private static short[] Tone(double hz, int samples, int rate = 48000) =>
			Enumerable.Range(0, samples).Select(i => (short)(Math.Sin(2 * Math.PI * hz * i / rate) * 16000)).ToArray();

		[Test]
		public void NormalizedStrength_PeaksAtToneFrequency()
		{
			var tone = Tone(1000, 4800);
			var onFreq = Goertzel.NormalizedStrength(tone, 1000, 48000);
			var offFreq = Goertzel.NormalizedStrength(tone, 2500, 48000);

			onFreq.Should().BeGreaterThan(0.5);
			onFreq.Should().BeGreaterThan(offFreq);
		}
	}

	[TestFixture]
	public class Mdc1200Tests
	{
		private static short[] Pad(short[] core) =>
			new short[2000].Concat(core).Concat(new short[2000]).ToArray();

		[Test]
		public void EncodeDecode_RoundTrips()
		{
			var settings = new Mdc1200Settings();
			var packet = new Mdc1200Packet { Op = 0x06, Arg = 0x12, UnitId = 0xABCD };
			var wave = Pad(Mdc1200Codec.Encode(packet, settings, 48000));

			var decoded = Mdc1200Codec.TryDecode(wave, settings, 48000);

			decoded.Should().NotBeNull();
			decoded.Op.Should().Be(0x06);
			decoded.Arg.Should().Be(0x12);
			decoded.UnitId.Should().Be(0xABCD);
			decoded.IsEmergency(settings).Should().BeTrue();
		}

		[Test]
		public void TryDecode_RejectsNoise()
		{
			var rnd = new short[20000];
			for (int i = 0; i < rnd.Length; i++)
				rnd[i] = (short)((i * 7919) % 1000 - 500); // deterministic pseudo-noise
			Mdc1200Codec.TryDecode(rnd, new Mdc1200Settings(), 48000).Should().BeNull();
		}

		[Test]
		public void Crc16Ccitt_DetectsCorruption()
		{
			var a = Mdc1200Codec.Crc16Ccitt(new byte[] { 0x06, 0x00, 0x12, 0x34 });
			var b = Mdc1200Codec.Crc16Ccitt(new byte[] { 0x06, 0x00, 0x12, 0x35 });
			a.Should().NotBe(b);
		}

		[Test]
		public void StreamingDecoder_RaisesPacket()
		{
			var settings = new Mdc1200Settings();
			var packet = new Mdc1200Packet { Op = 0x01, Arg = 0x00, UnitId = 0x1234 };
			var wave = Pad(Mdc1200Codec.Encode(packet, settings, 48000));

			var decoder = new Mdc1200Decoder(settings, 48000);
			Mdc1200Packet got = null;
			decoder.PacketDecoded += (_, p) => got = p;

			foreach (var chunk in wave.Chunk(480))
				decoder.Process(chunk);

			got.Should().NotBeNull();
			got.UnitId.Should().Be(0x1234);
		}
	}

	[TestFixture]
	public class SquelchGateTests
	{
		private static short[] LevelFrame(double dbfs, int samples = 480)
		{
			double amp = Math.Pow(10, dbfs / 20.0) * short.MaxValue;
			return Enumerable.Range(0, samples)
				.Select(i => (short)(Math.Sin(2 * Math.PI * 1000 * i / 48000.0) * amp * Math.Sqrt(2)))
				.ToArray();
		}

		[Test]
		public void Gate_OpensAboveOpenThreshold()
		{
			var gate = new SquelchGate(new SquelchSettings { OpenDbfs = -38, CloseDbfs = -45, HangMs = 100 });
			gate.Process(LevelFrame(-20)).Should().BeTrue();
		}

		[Test]
		public void Gate_StaysClosedBelowOpenThreshold()
		{
			var gate = new SquelchGate(new SquelchSettings { OpenDbfs = -38, CloseDbfs = -45, HangMs = 100 });
			gate.Process(LevelFrame(-60)).Should().BeFalse();
		}

		[Test]
		public void Gate_HangKeepsOpenThenCloses()
		{
			var gate = new SquelchGate(new SquelchSettings { OpenDbfs = -38, CloseDbfs = -45, HangMs = 30 });
			gate.Process(LevelFrame(-20)).Should().BeTrue(); // open, hang = 30 ms

			// 480 samples @ 48 kHz = 10 ms per frame; below the close threshold the
			// hang counts down 30 -> 20 -> 10 -> 0 (closes when it reaches 0).
			gate.Process(LevelFrame(-60)).Should().BeTrue();  // hang 20 ms
			gate.Process(LevelFrame(-60)).Should().BeTrue();  // hang 10 ms
			gate.Process(LevelFrame(-60)).Should().BeFalse(); // hang expired -> closed
		}
	}

	[TestFixture]
	public class ToneGeneratorTests
	{
		[Test]
		public void Sine_HasExpectedLengthAndFrequency()
		{
			var gen = new ToneGenerator(48000);
			var tone = gen.Sine(1000, 100); // 100 ms
			tone.Length.Should().Be(4800);
			Goertzel.NormalizedStrength(tone, 1000, 48000).Should().BeGreaterThan(0.4);
		}

		[Test]
		public void BuildAlert_TwoTone_ConcatenatesBothTones()
		{
			var gen = new ToneGenerator(48000);
			var profile = new ToneProfile
			{
				Type = ToneType.TwoToneSequential,
				ToneAFrequency = 1000, ToneADurationMs = 100,
				ToneBFrequency = 2000, ToneBDurationMs = 200,
				PreSpeechSilenceMs = 0
			};
			var alert = gen.BuildAlert(profile);
			alert.Length.Should().Be(48000 / 1000 * 300); // 100 ms + 200 ms
		}

		[Test]
		public void BuildAlert_None_IsEmpty()
		{
			new ToneGenerator().BuildAlert(new ToneProfile { Type = ToneType.None }).Should().BeEmpty();
		}
	}
}
