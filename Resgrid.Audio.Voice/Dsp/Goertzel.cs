using System;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>
	/// Goertzel single-frequency power estimator — an efficient way to measure the
	/// energy at one target frequency without a full FFT. Used for CTCSS/PL detection,
	/// emergency-tone detection, and FFSK bit slicing.
	/// </summary>
	public static class Goertzel
	{
		/// <summary>Magnitude-squared of <paramref name="frequencyHz"/> within the buffer.</summary>
		public static double Power(ReadOnlySpan<short> samples, double frequencyHz, int sampleRate)
		{
			if (samples.Length == 0)
				return 0;

			double omega = 2.0 * Math.PI * frequencyHz / sampleRate;
			double coeff = 2.0 * Math.Cos(omega);
			double s0, s1 = 0, s2 = 0;

			for (int i = 0; i < samples.Length; i++)
			{
				s0 = (samples[i] / 32768.0) + coeff * s1 - s2;
				s2 = s1;
				s1 = s0;
			}

			return s1 * s1 + s2 * s2 - coeff * s1 * s2;
		}

		/// <summary>
		/// Normalized tone strength (0..~1): the target-frequency power relative to the
		/// buffer's total energy. Robust to overall level so a single threshold works
		/// across loud and quiet signals.
		/// </summary>
		public static double NormalizedStrength(ReadOnlySpan<short> samples, double frequencyHz, int sampleRate)
		{
			if (samples.Length == 0)
				return 0;

			double total = 0;
			for (int i = 0; i < samples.Length; i++)
			{
				double s = samples[i] / 32768.0;
				total += s * s;
			}
			if (total <= 1e-12)
				return 0;

			double power = Power(samples, frequencyHz, sampleRate);
			// Goertzel power for a windowed pure tone ~= (N/2)^2 * amplitude^2; normalize
			// against total energy (N * meanSquare) to get a 0..1-ish ratio.
			double normalized = power / (total * samples.Length / 4.0);
			return normalized < 0 ? 0 : (normalized > 1 ? 1 : normalized);
		}
	}
}
