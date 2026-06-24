using System;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>
	/// A stateful one-pole high-pass filter. Removes DC, hum and sub-audible CTCSS/PL
	/// tones from the relayed audio so they don't bleed onto the destination network.
	/// </summary>
	public sealed class HighPassFilter
	{
		private readonly double _alpha;
		private double _prevIn;
		private double _prevOut;

		public HighPassFilter(double cutoffHz, int sampleRate)
		{
			double rc = 1.0 / (2 * Math.PI * Math.Max(1, cutoffHz));
			double dt = 1.0 / sampleRate;
			_alpha = rc / (rc + dt);
		}

		/// <summary>Filters a frame in place.</summary>
		public void Process(short[] samples)
		{
			if (samples == null)
				return;

			for (int i = 0; i < samples.Length; i++)
			{
				double x = samples[i];
				double y = _alpha * (_prevOut + x - _prevIn);
				_prevIn = x;
				_prevOut = y;

				if (y > short.MaxValue) y = short.MaxValue;
				else if (y < short.MinValue) y = short.MinValue;
				samples[i] = (short)y;
			}
		}

		public void Reset()
		{
			_prevIn = 0;
			_prevOut = 0;
		}
	}

	/// <summary>
	/// A simple peak limiter that prevents clipping/over-deviation when audio is
	/// boosted toward the radio or the channel.
	/// </summary>
	public sealed class SoftLimiter
	{
		private readonly double _threshold;

		public SoftLimiter(double thresholdDbfs = -1.0)
		{
			_threshold = Math.Pow(10, thresholdDbfs / 20.0) * short.MaxValue;
		}

		public void Process(short[] samples)
		{
			if (samples == null)
				return;

			for (int i = 0; i < samples.Length; i++)
			{
				double v = samples[i];
				if (v > _threshold) v = _threshold;
				else if (v < -_threshold) v = -_threshold;
				samples[i] = (short)v;
			}
		}
	}
}
