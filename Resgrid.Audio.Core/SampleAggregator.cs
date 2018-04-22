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
			int SAMPLE_RESOLUTION = 16;
			int BYTES_PER_POINT = SAMPLE_RESOLUTION / 8;

			Int32[] vals = new Int32[buffer.Length / BYTES_PER_POINT];
			double[] Ys = new double[buffer.Length / BYTES_PER_POINT];
			//string[] Xs = new string[buffer.Length / BYTES_PER_POINT];

			double[] Ys2 = new double[buffer.Length / BYTES_PER_POINT];
			//string[] Xs2 = new string[buffer.Length / BYTES_PER_POINT];


			for (int i = 0; i < vals.Length; i++)
			{
				// bit shift the byte buffer into the right variable format
				byte hByte = buffer[i * 2 + 1];
				byte lByte = buffer[i * 2 + 0];
				vals[i] = (int)(short)((hByte << 8) | lByte);
				//Xs[i] = i.ToString();
				Ys[i] = vals[i];
				//Xs2[i] = ((double)i / Ys.Length * RATE / 1000.0).ToString(); // units are in kHz
			}

			Ys2 = Functions.FFT(Ys);

			if (WaveformCalculated != null)
			{
				WaveformCalculated(this, new WaveformEventArgs(Ys.Cast<Object>(), Ys2.Take(Ys2.Length / 2).Cast<Object>()));
			}
		}
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
		public WaveformEventArgs(IEnumerable<Object> pulseCodeModulation, IEnumerable<Object> fastFourierTransform)
		{
			PulseCodeModulation = pulseCodeModulation;
			FastFourierTransform = fastFourierTransform;
		}

		/// <summary>
		/// Time Domain
		/// </summary>
		public IEnumerable<Object> PulseCodeModulation { get; private set; }

		/// <summary>
		/// Frequency Domain
		/// </summary>
		public IEnumerable<Object> FastFourierTransform { get; private set; }
	}
}
