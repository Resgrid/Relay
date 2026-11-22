using System.Collections.Generic;
using System.Linq;
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
		public void Hang_IsTimedFromTheLastWindowWithTheTone_HoweverTheAudioIsBuffered()
		{
			// The same audio, 1.2 s with the tone then 500 ms without, fed as 20 ms frames and with its tail in one 700 ms buffer.
			// The tone was last seen at most ~500 ms back, inside the 600 ms hang, so both must be open; the large buffer used to
			// close because all of it was counted against the hang.
			var frames = new CtcssDetector(100, 0.30, AudioFormat.SampleRate, hangMs: 600);
			var buffered = new CtcssDetector(100, 0.30, AudioFormat.SampleRate, hangMs: 600);
			Feed(frames, Pl, 50).Should().BeTrue();
			Feed(buffered, Pl, 50).Should().BeTrue();

			Feed(frames, Pl, 10);
			Feed(frames, NoPl, 25).Should().BeTrue();
			buffered.Process(Repeat(Pl, 10).Concat(Repeat(NoPl, 25)).ToArray()).Should().BeTrue("the tone ended under 600 ms ago, however the audio was buffered");
			buffered.IsPresent.Should().BeFalse();

			// 300 ms more puts both past the hang.
			Feed(frames, NoPl, 15).Should().BeFalse();
			buffered.Process(Repeat(NoPl, 15).ToArray()).Should().BeFalse();
		}

		private static IEnumerable<short> Repeat(short[] frame, int count) => Enumerable.Repeat(frame, count).SelectMany(samples => samples);

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
