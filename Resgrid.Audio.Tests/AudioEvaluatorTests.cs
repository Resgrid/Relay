using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Tests
{
	/*
	[TestFixture]
    public class AudioEvaluatorTests
    {
		[Test]
		public void EvaluateAudioTrigger_BasicShouldReturnTrue()
		{
			var audioEvaluator = new AudioEvaluator();

			var trigger = new Trigger();
			trigger.Frequency1 = 50;
			trigger.Tolerance = 0;
			trigger.Count = 1;
			trigger.Time1 = .00004;
			double[] sample = new double[10];
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

	    [Test]
	    public void EvaluateAudioTrigger_BasicShouldReturnFalse()
	    {
		    var audioEvaluator = new AudioEvaluator();

		    var trigger = new Trigger();
		    trigger.Frequency1 = 50;
		    trigger.Tolerance = 0;
			trigger.Count = 1;
		    trigger.Time1 = .00004;
		    double[] sample = new double[10];
			sample[0] = 103;
		    sample[1] = 12;
		    sample[2] = 31;
		    sample[3] = 66;
		    sample[4] = 50;
		    sample[5] = 99;
		    sample[6] = 100;
		    sample[7] = 50;
		    sample[8] = 1;
		    sample[9] = 0;

		    var result = audioEvaluator.EvaluateAudioTrigger(trigger, sample);

		    result.Should().BeFalse();
	    }

	    [Test]
	    public void EvaluateAudioTrigger_HighTimeShouldReturnFalse()
	    {
		    var audioEvaluator = new AudioEvaluator();

		    var trigger = new Trigger();
		    trigger.Frequency1 = 50;
		    trigger.Tolerance = 0;
		    trigger.Count = 1;
		    trigger.Time1 = .0008;
		    double[] sample = new double[10];
			sample[0] = 103;
		    sample[1] = 12;
		    sample[2] = 31;
		    sample[3] = 66;
		    sample[4] = 50;
		    sample[5] = 99;
		    sample[6] = 100;
		    sample[7] = 50;
		    sample[8] = 1;
		    sample[9] = 0;

		    var result = audioEvaluator.EvaluateAudioTrigger(trigger, sample);

		    result.Should().BeFalse();
	    }

	    [Test]
	    public void EvaluateAudioTrigger_ToleranceShouldReturnTrue()
	    {
		    var audioEvaluator = new AudioEvaluator();

		    var trigger = new Trigger();
		    trigger.Frequency1 = 50;
		    trigger.Tolerance = 10;
		    trigger.Count = 1;
		    trigger.Time1 = .00004;
		    double[] sample = new double[10];
			sample[0] = 51;
		    sample[1] = 12;
		    sample[2] = 31;
		    sample[3] = 54;
		    sample[4] = 52;
		    sample[5] = 99;
		    sample[6] = 100;
		    sample[7] = 50;
		    sample[8] = 1;
		    sample[9] = 0;

			var result = audioEvaluator.EvaluateAudioTrigger(trigger, sample);

		    result.Should().BeTrue();
	    }

	    [Test]
	    public void EvaluateAudioTrigger_TightToleranceShouldReturnFalse()
	    {
		    var audioEvaluator = new AudioEvaluator();

		    var trigger = new Trigger();
		    trigger.Frequency1 = 50;
		    trigger.Tolerance = 2;
		    trigger.Count = 1;
		    trigger.Time1 = .00004;
		    double[] sample = new double[10];
			sample[0] = 57;
		    sample[1] = 12;
		    sample[2] = 31;
		    sample[3] = 54;
		    sample[4] = 59;
		    sample[5] = 99;
		    sample[6] = 100;
		    sample[7] = 50;
		    sample[8] = 1;
		    sample[9] = 0;

		    var result = audioEvaluator.EvaluateAudioTrigger(trigger, sample);

		    result.Should().BeFalse();
	    }
	}
	*/
}
