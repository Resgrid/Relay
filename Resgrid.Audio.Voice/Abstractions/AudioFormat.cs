using System;

namespace Resgrid.Audio.Voice.Abstractions
{
	/// <summary>
	/// The canonical PCM audio format used across the voice engine. LiveKit's WebRTC
	/// audio is Opus at 48 kHz; we publish and consume raw PCM16 mono at 48 kHz and
	/// resample to/from the radio device rate at the edges.
	/// </summary>
	public static class AudioFormat
	{
		/// <summary>48 kHz — the WebRTC/Opus rate LiveKit uses.</summary>
		public const int SampleRate = 48000;

		/// <summary>Mono. PTT radio audio is single channel.</summary>
		public const int Channels = 1;

		/// <summary>10 ms frame — the natural Opus/WebRTC frame interval.</summary>
		public const int FrameMilliseconds = 10;

		/// <summary>Samples per 10 ms mono frame at 48 kHz = 480.</summary>
		public const int SamplesPerFrame = SampleRate / 1000 * FrameMilliseconds; // 480

		/// <summary>Bytes per sample for PCM16.</summary>
		public const int BytesPerSample = 2;

		/// <summary>Computes the RMS amplitude (0..1) of a PCM16 mono buffer.</summary>
		public static double Rms(ReadOnlySpan<short> samples)
		{
			if (samples.Length == 0)
				return 0;

			double sumSquares = 0;
			for (int i = 0; i < samples.Length; i++)
			{
				double s = samples[i] / 32768.0;
				sumSquares += s * s;
			}

			return Math.Sqrt(sumSquares / samples.Length);
		}

		/// <summary>RMS expressed in dBFS (full scale). Silence ≈ -inf, clipped ≈ 0 dB.</summary>
		public static double Dbfs(ReadOnlySpan<short> samples)
		{
			var rms = Rms(samples);
			if (rms <= 1e-9)
				return -100.0;

			return 20.0 * Math.Log10(rms);
		}
	}
}
