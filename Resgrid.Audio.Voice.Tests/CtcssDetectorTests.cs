using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Dsp;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class CtcssDetectorTests
	{
		private static readonly short[] Pl = TestAudio.Tone(20, frequency: 100, amplitude: 8000);
		private static readonly short[] NoPl = TestAudio.Tone(20, frequency: 1000, amplitude: 8000);

		private static bool Feed(CtcssDetector detector, short[] frame, int frames)
		{
			bool open = false;
			for (int i = 0; i < frames; i++)
				open = detector.Process(frame);
			return open;
		}

		[Test]
		public void OpensOnThePlTone()
		{
			var detector = new CtcssDetector(100, 0.30, AudioFormat.SampleRate);
			Feed(detector, Pl, 50).Should().BeTrue();
			detector.IsPresent.Should().BeTrue();
		}

		[Test]
		public void Hang_HoldsTheSquelchOpenAfterTheToneDrops_ThenCloses()
		{
			var withHang = new CtcssDetector(100, 0.30, AudioFormat.SampleRate, hangMs: 600);
			var noHang = new CtcssDetector(100, 0.30, AudioFormat.SampleRate);
			Feed(withHang, Pl, 50).Should().BeTrue();
			Feed(noHang, Pl, 50).Should().BeTrue();

			// 300 ms without the tone (as on a loud syllable that swamps it).
			Feed(withHang, NoPl, 15).Should().BeTrue("the hang bridges a short loss of the tone");
			Feed(noHang, NoPl, 15).Should().BeFalse();
			withHang.IsPresent.Should().BeFalse();

			// Well past the hang.
			Feed(withHang, NoPl, 45).Should().BeFalse();
		}

		[Test]
		public void Reset_ClosesImmediately()
		{
			var detector = new CtcssDetector(100, 0.30, AudioFormat.SampleRate, hangMs: 600);
			Feed(detector, Pl, 50).Should().BeTrue();

			detector.Reset();

			detector.IsOpen.Should().BeFalse();
			detector.Process(NoPl).Should().BeFalse();
		}

		[Test]
		public void NoFrequency_NeverOpens()
		{
			var detector = new CtcssDetector(0, 0.30, AudioFormat.SampleRate, hangMs: 600);
			Feed(detector, Pl, 50).Should().BeFalse();
		}
	}
}
