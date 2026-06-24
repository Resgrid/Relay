using System;
using System.Collections.Generic;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>
	/// Detects a sub-audible CTCSS/PL tone (67–254 Hz) using Goertzel over a long
	/// window (low frequencies need many samples to resolve). Used as a squelch source
	/// so the relay only opens when the correct PL tone is present — rejecting other
	/// users and noise on a shared channel.
	/// </summary>
	public sealed class CtcssDetector
	{
		private readonly double _frequency;
		private readonly double _minStrength;
		private readonly int _sampleRate;
		private readonly int _windowSamples;
		private readonly List<short> _window = new List<short>();
		private bool _present;

		public CtcssDetector(double frequencyHz, double minStrength, int sampleRate, int windowMs = 200)
		{
			_frequency = frequencyHz;
			_minStrength = minStrength;
			_sampleRate = sampleRate;
			_windowSamples = Math.Max(sampleRate * windowMs / 1000, sampleRate / 20);
		}

		public bool IsPresent => _present;

		/// <summary>Feeds audio; returns the latest tone-present state.</summary>
		public bool Process(ReadOnlySpan<short> pcm)
		{
			if (_frequency <= 0)
			{
				_present = false;
				return false;
			}

			for (int i = 0; i < pcm.Length; i++)
				_window.Add(pcm[i]);

			while (_window.Count >= _windowSamples)
			{
				var block = _window.GetRange(0, _windowSamples).ToArray();
				_window.RemoveRange(0, _windowSamples / 2); // 50% overlap for responsiveness
				var strength = Goertzel.NormalizedStrength(block, _frequency, _sampleRate);
				_present = strength >= _minStrength;
			}

			return _present;
		}
	}
}
