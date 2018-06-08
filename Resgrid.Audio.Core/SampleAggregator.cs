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
			if (DataAvailable != null)
			{
				DataAvailable(this, new DataAvailableArgs(buffer, bytesRecorded));
			}
		}

		public void Add(float value)
		{
			maxValue = Math.Max(maxValue, value);
			minValue = Math.Min(minValue, value);
			count++;
			if (count >= NotificationCount && NotificationCount > 0)
			{
				if (MaximumCalculated != null)
				{
					MaximumCalculated(this, new MaxSampleEventArgs(minValue, maxValue));
				}
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
		public MaxSampleEventArgs(float minValue, float maxValue)
		{
			MaxSample = maxValue;
			MinSample = minValue;
		}
		public float MaxSample { get; private set; }
		public float MinSample { get; private set; }
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
		/// Time Domain
		/// </summary>
		public double[] PulseCodeModulation { get; private set; }

		/// <summary>
		/// Frequency Domain
		/// </summary>
		public double[] FastFourierTransform { get; private set; }
	}
}
