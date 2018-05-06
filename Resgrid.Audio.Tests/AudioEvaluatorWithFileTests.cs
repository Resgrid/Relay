using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using NAudio.Wave;
using NUnit.Framework;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
    public class AudioEvaluatorWithFileTests
	{
		[Test]
		public void EvaluateAudioTrigger_BasicShouldReturnTrue()
		{
			
			var path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");

			//WaveFileReader reader = new WaveFileReader(path + "\\Data\\TestAudio.wav");

			List<byte> testAudio1; // = File.ReadAllBytes(path + "\\Data\\TestAudio.wav").ToList();
			int BUFFERSIZE = (int)Math.Pow(2, 14); // must be a multiple of 2

			using (var reader = new AudioFileReader(path + "\\Data\\TestAudio.wav"))
			{
				// find the max peak
				byte[] buffer = new byte[BUFFERSIZE];
				int read;
				do
				{
					read = reader.Read(buffer, 0, buffer.Length);
				} while (read > 0);

				testAudio1 = new List<byte>(buffer);
			}

			var audioEvaluator = new AudioEvaluator();

			if (testAudio1.Count() % 2 != 0)
			{
				testAudio1.RemoveAt(testAudio1.Count() - 1);
			}

			var trigger = new Trigger();
			trigger.Frequency1 = 645.7;
			trigger.Frequency2 = 716.10;
			trigger.Tolerance = 10;
			trigger.Count = 2;
			trigger.Time = .8;

			var result = audioEvaluator.EvaluateAudioTrigger(trigger, Functions.WaveDataToFFT(testAudio1.ToArray()));

			result.Should().BeTrue();
		}
	}
}
