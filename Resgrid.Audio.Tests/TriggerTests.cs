using System;
using System.Collections.Generic;
using DtmfDetection;
using DtmfDetection.NAudio;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class TriggerTests
	{
		private static readonly DateTime PageStart = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

		private static DtmfToneEnd Tone(int hertz, int durationMs, double secondsAfterStart)
		{
			var tone = new DtmfToneEnd(new DtmfTone(hertz, 0, PhoneKey.Custom1), 0, TimeSpan.FromMilliseconds(durationMs));
			// The detector stamps the tone when it ends (private setter); pin it so the order is deterministic.
			typeof(DtmfToneEnd).GetProperty(nameof(DtmfToneEnd.TimeStamp)).SetValue(tone, PageStart.AddSeconds(secondsAfterStart));
			return tone;
		}

		[Test]
		public void A_Two_Tone_Trigger_Should_Match_Its_Two_Tones()
		{
			var trigger = new Trigger { Count = 2, Frequency1 = 368.5, Frequency2 = 433.7, Time1 = 1000, Time2 = 3000 };
			var first = Tone(369, 1000, 0);
			var second = Tone(434, 3000, 3);

			var matched = trigger.GetMatchingTones(new List<DtmfToneEnd> { first, second });

			matched.Should().HaveCount(2);
			matched[0].Should().BeSameAs(first);
			matched[1].Should().BeSameAs(second);
		}

		[Test]
		public void One_Tone_Should_Not_Fill_Both_Slots_When_Both_Frequencies_Round_To_The_Same_Tone()
		{
			// 368.5 Hz and 369.4 Hz are both detected as 369 Hz: a single 600 ms tone met both 500 ms thresholds and paged.
			var trigger = new Trigger { Count = 2, Frequency1 = 368.5, Frequency2 = 369.4, Time1 = 500, Time2 = 500 };

			trigger.GetMatchingTones(new List<DtmfToneEnd> { Tone(369, 600, 0) }).Should().BeNull();
		}

		[Test]
		public void Two_Separate_Tones_Should_Match_When_Both_Frequencies_Round_To_The_Same_Tone()
		{
			var trigger = new Trigger { Count = 2, Frequency1 = 368.5, Frequency2 = 369.4, Time1 = 500, Time2 = 500 };
			var first = Tone(369, 600, 0);
			var second = Tone(369, 600, 2);

			var matched = trigger.GetMatchingTones(new List<DtmfToneEnd> { first, second });

			matched.Should().HaveCount(2);
			matched[0].Should().BeSameAs(first);
			matched[1].Should().BeSameAs(second);
		}
	}
}
