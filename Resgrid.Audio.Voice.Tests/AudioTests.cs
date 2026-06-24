using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.Audio;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class MuLawTests
	{
		[Test]
		public void Decode_0xFF_IsNearZero()
		{
			MuLaw.Decode(0xFF).Should().BeInRange((short)-50, (short)50);
		}

		[Test]
		public void Decode_ProducesOppositeSignsForSignBit()
		{
			// 0x00 and 0x80 are the largest-magnitude codes with opposite sign bits.
			var negative = MuLaw.Decode(0x00);
			var positive = MuLaw.Decode(0x80);
			negative.Should().BeLessThan(0);
			positive.Should().BeGreaterThan(0);
		}
	}

	[TestFixture]
	public class ResamplerTests
	{
		[Test]
		public void Resample_SameRate_ReturnsEquivalentCopy()
		{
			var input = new short[] { 1, 2, 3, 4, 5 };
			var output = Resampler.Resample(input, 48000, 48000);
			output.Should().Equal(input);
			output.Should().NotBeSameAs(input);
		}

		[Test]
		public void Resample_8kTo48k_SixfoldsLength()
		{
			var input = new short[800]; // 100 ms @ 8 kHz
			var output = Resampler.Resample(input, 8000, 48000);
			output.Length.Should().BeInRange(4790, 4800); // ~6x
		}

		[Test]
		public void Resample_PreservesToneFrequency()
		{
			// A 1 kHz tone at 8 kHz, upsampled to 48 kHz, must still read as 1 kHz.
			var tone = Enumerable.Range(0, 1600)
				.Select(i => (short)(Math.Sin(2 * Math.PI * 1000 * i / 8000.0) * 16000)).ToArray();
			var up = Resampler.Resample(tone, 8000, 48000);

			var atTone = Resgrid.Audio.Voice.Dsp.Goertzel.NormalizedStrength(up, 1000, 48000);
			var offTone = Resgrid.Audio.Voice.Dsp.Goertzel.NormalizedStrength(up, 3000, 48000);
			atTone.Should().BeGreaterThan(offTone);
		}

		[Test]
		public void ApplyGain_ScalesAndClips()
		{
			var samples = new short[] { 1000, -1000, 20000 };
			Resampler.ApplyGain(samples, 2.0);
			samples[0].Should().Be(2000);
			samples[1].Should().Be(-2000);
			samples[2].Should().Be(short.MaxValue); // clipped
		}
	}

	[TestFixture]
	public class WavIoTests
	{
		[Test]
		public void WritePcm16_ThenRead_RoundTrips()
		{
			var pcm = Enumerable.Range(0, 1000).Select(i => (short)((i % 200) - 100)).ToArray();
			var wav = WavIo.WritePcm16(pcm, 48000, 1);

			// 44-byte header + 2 bytes/sample.
			wav.Length.Should().Be(44 + pcm.Length * 2);

			var (samples, rate) = WavIo.ReadToPcm16Mono(wav);
			rate.Should().Be(48000);
			samples.Should().Equal(pcm);
		}
	}
}
