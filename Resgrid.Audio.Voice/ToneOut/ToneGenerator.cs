using System;
using System.Collections.Generic;
using Resgrid.Audio.Voice.Abstractions;

namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// Synthesizes alert tones (sine-based) at the engine rate (48 kHz mono PCM16):
	/// two-tone sequential paging, hi/lo warble, single tones, silence, and a
	/// courtesy beep. Pure DSP — no device or SDK dependencies, so it is unit-testable.
	/// </summary>
	public sealed class ToneGenerator
	{
		private readonly int _sampleRate;

		public ToneGenerator(int sampleRate = AudioFormat.SampleRate)
		{
			_sampleRate = sampleRate;
		}

		/// <summary>A pure sine tone with short raised-cosine fades to avoid clicks.</summary>
		public short[] Sine(double frequencyHz, int milliseconds, double amplitude = 0.5)
		{
			int count = Math.Max(0, _sampleRate * milliseconds / 1000);
			var samples = new short[count];
			double step = 2 * Math.PI * frequencyHz / _sampleRate;
			int fade = Math.Min(count / 2, _sampleRate / 200); // ~5 ms fade

			for (int i = 0; i < count; i++)
			{
				double env = 1.0;
				if (i < fade) env = 0.5 * (1 - Math.Cos(Math.PI * i / fade));
				else if (i >= count - fade) env = 0.5 * (1 - Math.Cos(Math.PI * (count - 1 - i) / fade));

				double value = Math.Sin(i * step) * amplitude * env;
				samples[i] = (short)(value * short.MaxValue);
			}
			return samples;
		}

		public short[] Silence(int milliseconds)
		{
			int count = Math.Max(0, _sampleRate * milliseconds / 1000);
			return new short[count];
		}

		/// <summary>A short courtesy beep (e.g. a repeater "K" tone after unkey).</summary>
		public short[] CourtesyBeep(double frequencyHz = 660, int milliseconds = 120, double amplitude = 0.4)
			=> Sine(frequencyHz, milliseconds, amplitude);

		/// <summary>Builds the full alert preamble for a <see cref="ToneProfile"/>.</summary>
		public short[] BuildAlert(ToneProfile profile)
		{
			if (profile == null || profile.Type == ToneType.None)
				return Array.Empty<short>();

			var buffer = new List<short>();
			switch (profile.Type)
			{
				case ToneType.TwoToneSequential:
					buffer.AddRange(Sine(profile.ToneAFrequency, profile.ToneADurationMs, profile.Amplitude));
					buffer.AddRange(Sine(profile.ToneBFrequency, profile.ToneBDurationMs, profile.Amplitude));
					break;

				case ToneType.HiLo:
					for (int i = 0; i < profile.HiLoCycles; i++)
					{
						buffer.AddRange(Sine(profile.ToneAFrequency, profile.HiLoSegmentMs, profile.Amplitude));
						buffer.AddRange(Sine(profile.ToneBFrequency, profile.HiLoSegmentMs, profile.Amplitude));
					}
					break;

				case ToneType.SingleTone:
					buffer.AddRange(Sine(profile.ToneAFrequency, profile.ToneADurationMs, profile.Amplitude));
					break;
			}

			if (profile.PreSpeechSilenceMs > 0)
				buffer.AddRange(Silence(profile.PreSpeechSilenceMs));

			return buffer.ToArray();
		}
	}
}
