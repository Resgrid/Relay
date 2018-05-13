using System;
using System.Collections.Generic;
using System.ComponentModel;
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

			//System.IO.FileStream WaveFile = System.IO.File.OpenRead(path + "\\Data\\TestAudio.wav");
			//byte[] data = new byte[WaveFile.Length];
			int sampleRate;
			//double[] fileData = Functions.WaveFileDataPrepare(path + "\\Data\\TestAudio.wav", out sampleRate);''

			List<double> fftData = new List<double>();

			int BUFFERSIZE = (int)Math.Pow(2, 11); // must be a multiple of 2
			WaveStream waveStream = new NAudio.Wave.WaveFileReader(path + "\\Data\\TestAudio.wav");
			int bytesPerSample = (waveStream.WaveFormat.BitsPerSample / 8) * waveStream.WaveFormat.Channels;
			int bytesRead = 0;

			BufferedWaveProvider bwp = new BufferedWaveProvider(new WaveFormat(44100, 1));
			bwp.BufferLength = BUFFERSIZE * 2;
			bwp.DiscardOnBufferOverflow = true;

			byte[] waveData = new byte[BUFFERSIZE * 2];
			bytesRead = waveStream.Read(waveData, 0, 128 * bytesPerSample);
			bwp.AddSamples(waveData, 0, bytesRead);

			int frameSize = BUFFERSIZE;
			byte[] frames = new byte[frameSize];
			bwp.Read(frames, 0, frameSize);

			fftData.AddRange(Functions.FFT(frames.Cast<double>().ToArray()));

			while (bytesRead != 0)
			{
				bytesRead = waveStream.Read(waveData, bytesRead, 128 * bytesPerSample);
				bwp.AddSamples(waveData, 0, bytesRead);

				frameSize = BUFFERSIZE;
				frames = new byte[frameSize];
				bwp.Read(frames, 0, frameSize);

				fftData.AddRange(Functions.FFT(frames.Cast<double>().ToArray()));
			}





			//WavFile wavFile = new WavFile();
			//wavFile.ReadWav(path + "\\Data\\TestAudio.wav");

			//WaveFileReader reader = new WaveFileReader(path + "\\Data\\TestAudio.wav");
			//List<byte> testAudio1; // = File.ReadAllBytes(path + "\\Data\\TestAudio.wav").ToList();
			//int BUFFERSIZE = (int)Math.Pow(2, 14); // must be a multiple of 2

			//using (var reader = new AudioFileReader(path + "\\Data\\TestAudio.wav"))
			//{
			//	// find the max peak
			//	byte[] buffer = new byte[BUFFERSIZE];
			//	int read;
			//	do
			//	{
			//		read = reader.Read(buffer, 0, buffer.Length);
			//	} while (read > 0);

			//	testAudio1 = new List<byte>(buffer);
			//}

			var audioEvaluator = new AudioEvaluator();

			//if (testAudio1.Count() % 2 != 0)
			//{
			//	testAudio1.RemoveAt(testAudio1.Count() - 1);
			//}

			var trigger = new Trigger();
			trigger.Frequency1 = 645.7;
			trigger.Frequency2 = 716.10;
			trigger.Tolerance = 10;
			trigger.Count = 2;
			trigger.Time = .8;

			//wavFile.FFT(false);

			//var fftData = Functions.FFT(fileData);
			//var fftData = wavFile._aSamples;
			var result = audioEvaluator.EvaluateAudioTrigger(trigger, fftData.ToArray());

			result.Should().BeTrue();
		}
	}
}
