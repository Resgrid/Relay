// https://github.com/markheath/voicerecorder

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Resgrid.Audio.Core
{
	public class SampleAggregator
	{
		private int RATE = 44100; // sample rate of the sound card
		private int BUFFERSIZE = (int)Math.Pow(2, 11); // must be a multiple of 2

		public event EventHandler<DataAvailableArgs> DataAvailable;
		public event EventHandler<MaxSampleEventArgs> MaximumCalculated;
		public event EventHandler<WaveformEventArgs> WaveformCalculated;
		public event EventHandler Restart = delegate { };
		private float maxValue;
		private float minValue;
		public int NotificationCount { get; set; }
		int count;

		public void RaiseRestart()
		{
			Restart(this, EventArgs.Empty);
		}

		private void Reset()
		{
			count = 0;
			maxValue = minValue = 0;
		}

		public void OnDataAvailable(byte[] buffer, int bytesRecorded)
		{
			DataAvailable?.Invoke(this, new DataAvailableArgs(buffer, bytesRecorded));
		}

		public void Add(float value)
		{
			maxValue = Math.Max(maxValue, value);
			minValue = Math.Min(minValue, value);
			double dB = 20 * Math.Log10(Math.Abs(value));

			count++;
			if (count >= NotificationCount && NotificationCount > 0)
			{
				MaximumCalculated?.Invoke(this, new MaxSampleEventArgs(minValue, maxValue, dB));
				Reset();
			}
		}

		public void Calculate(byte[] buffer, int bytesRecorded)
		{
			var waveFormEventArgs = AudioFunctions.PrepareAudioData(buffer, bytesRecorded);

			if (waveFormEventArgs != null && WaveformCalculated != null)
			{
				WaveformCalculated(this, waveFormEventArgs);
			}
		}
	}

	public class DataAvailableArgs : EventArgs
	{
		[DebuggerStepThrough]
		public DataAvailableArgs(byte[] buffer, int bytesRecorded)
		{
			Buffer = buffer;
			BytesRecorded = bytesRecorded;
		}
		public byte[] Buffer { get; private set; }
		public int BytesRecorded { get; private set; }
	}

	public class MaxSampleEventArgs : EventArgs
	{
		[DebuggerStepThrough]
		public MaxSampleEventArgs(float minValue, float maxValue, double db)
		{
			MaxSample = maxValue;
			MinSample = minValue;
			Db = db;
		}
		public float MaxSample { get; private set; }
		public float MinSample { get; private set; }
		public double Db { get; private set; }
	}

	public class WaveformEventArgs : EventArgs
	{
		[DebuggerStepThrough]
		public WaveformEventArgs(double[] pulseCodeModulation, double[] fastFourierTransform)
		{
			PulseCodeModulation = pulseCodeModulation;
			FastFourierTransform = fastFourierTransform;
		}

		/// <summary>
		/// Time1 Domain
		/// </summary>
		public double[] PulseCodeModulation { get; private set; }

		/// <summary>
		/// Frequency Domain
		/// </summary>
		public double[] FastFourierTransform { get; private set; }
	}
}
