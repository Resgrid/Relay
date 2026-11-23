using System;
using System.Collections.Generic;
using DtmfDetection;
using DtmfDetection.NAudio;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using Serilog;

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

		// ─── The evaluator's tone bank, which merges the pieces of one tone before the triggers see them ───

		/// <summary>Feeds the tones to a real evaluator watching for <paramref name="trigger"/>; returns how often it paged.</summary>
		private static int Evaluate(Trigger trigger, params DtmfToneEnd[] tones)
		{
			var watcher = new Watcher { Name = "Station 5", Active = true, Code = "station5", Type = 2, Triggers = new List<Trigger> { trigger } };
			var evaluator = new AudioEvaluator(new LoggerConfiguration().CreateLogger());
			evaluator.Init(new Config { Watchers = new List<Watcher> { watcher } });
			var triggered = 0;
			evaluator.WatcherTriggered += (_, _) => triggered++;

			foreach (var tone in tones)
				evaluator.OnToneStopped(tone);

			return triggered;
		}

		[Test]
		public void Two_Separate_Bursts_800_Ms_Apart_Should_Page_When_Both_Frequencies_Round_To_The_Same_Tone()
		{
			// Inside the bank's 1 s merge window: the two bursts were merged into one occurrence, which the trigger then refused
			// to use for both slots, so this page never matched.
			var trigger = new Trigger { Count = 2, Frequency1 = 368.5, Frequency2 = 369.4, Time1 = 500, Time2 = 500 };

			Evaluate(trigger, Tone(369, 600, 0), Tone(369, 600, 0.8)).Should().Be(1);
		}

		[Test]
		public void One_Continuous_Tone_Should_Not_Page_When_Both_Frequencies_Round_To_The_Same_Tone()
		{
			var trigger = new Trigger { Count = 2, Frequency1 = 368.5, Frequency2 = 369.4, Time1 = 500, Time2 = 500 };

			Evaluate(trigger, Tone(369, 1300, 0)).Should().Be(0);
			// The same tone, lost by the detector for a moment: its short second piece is merged back into it.
			Evaluate(trigger, Tone(369, 600, 0), Tone(369, 300, 0.4)).Should().Be(0);
		}

		[Test]
		public void The_Pieces_Of_One_Tone_Should_Still_Be_Merged_For_Its_Duration()
		{
			var trigger = new Trigger { Count = 1, Frequency1 = 369, Time1 = 600 };

			Evaluate(trigger, Tone(369, 300, 0), Tone(369, 400, 0.45)).Should().Be(1, "300 ms + 400 ms of one tone meet its 600 ms");
		}
	}
}
