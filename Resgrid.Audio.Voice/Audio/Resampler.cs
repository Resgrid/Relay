using System;

namespace Resgrid.Audio.Voice.Audio
{
	/// <summary>
	/// Lightweight mono PCM16 sample-rate conversion (linear interpolation) plus
	/// gain/mix helpers. Linear interpolation is adequate for voice-band audio and
	/// keeps the engine free of OS-specific resamplers.
	/// </summary>
	public static class Resampler
	{
		/// <summary>Resamples mono PCM16 from <paramref name="inRate"/> to <paramref name="outRate"/>.</summary>
		public static short[] Resample(short[] input, int inRate, int outRate)
		{
			if (input == null || input.Length == 0)
				return Array.Empty<short>();
			if (inRate <= 0)
				throw new ArgumentOutOfRangeException(nameof(inRate), "Sample rate must be positive.");
			if (outRate <= 0)
				throw new ArgumentOutOfRangeException(nameof(outRate), "Sample rate must be positive.");
			if (inRate == outRate)
				return (short[])input.Clone();

			long outLen = (long)input.Length * outRate / inRate;
			if (outLen <= 0)
				return Array.Empty<short>();
			if (outLen > int.MaxValue)
				throw new ArgumentOutOfRangeException(nameof(outRate),
					$"Resampled length {outLen} exceeds the maximum array size ({int.MaxValue}).");

			var output = new short[(int)outLen];
			double step = (double)inRate / outRate;
			double pos = 0;

			for (long j = 0; j < outLen; j++)
			{
				int idx = (int)pos;
				double frac = pos - idx;
				int next = Math.Min(idx + 1, input.Length - 1);
				double sample = input[idx] * (1 - frac) + input[next] * frac;
				output[j] = ClampToShort(sample);
				pos += step;
			}

			return output;
		}

		/// <summary>Applies linear gain with hard clipping.</summary>
		public static void ApplyGain(short[] samples, double gain)
		{
			if (samples == null || Math.Abs(gain - 1.0) < 1e-6)
				return;
			for (int i = 0; i < samples.Length; i++)
				samples[i] = ClampToShort(samples[i] * gain);
		}

		/// <summary>Additively mixes <paramref name="source"/> into <paramref name="target"/> (in place) with clipping.</summary>
		public static void MixInto(short[] target, ReadOnlySpan<short> source)
		{
			int n = Math.Min(target.Length, source.Length);
			for (int i = 0; i < n; i++)
				target[i] = ClampToShort(target[i] + source[i]);
		}

		public static short ClampToShort(double value)
		{
			if (value > short.MaxValue) return short.MaxValue;
			if (value < short.MinValue) return short.MinValue;
			return (short)value;
		}
	}
}
