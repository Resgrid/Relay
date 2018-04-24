using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
    public class AudioEvaluatorTests
    {
		[Test]
		public void EvaluateAudioTrigger_BasicShouldReturnTrue()
		{
			var audioEvaluator = new AudioEvaluator();

			var trigger = new Trigger();
			trigger.Frequency1 = 50;
			trigger.Count = 1;
			trigger.Time = .00004;
			byte[] sample = new byte[10];
			sample[0] = 50;
			sample[1] = 12;
			sample[2] = 31;
			sample[3] = 50;
			sample[4] = 50;
			sample[5] = 99;
			sample[6] = 100;
			sample[7] = 50;
			sample[8] = 1;
			sample[9] = 0;

			var result = audioEvaluator.EvaluateAudioTrigger(trigger, sample);

			result.Should().BeTrue();
		}
	}
}
