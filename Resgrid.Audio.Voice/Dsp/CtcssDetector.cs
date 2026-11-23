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
		private readonly double _hangMs;
		private readonly List<short> _window = new List<short>();
		private bool _present;
		private bool _open;

		// Sample positions in the audio fed so far: how much there has been, where _window starts, and where the last analysis
		// window holding the tone ended. The hang is timed from that end, so whether the gate is open depends on the audio
		// since the tone was last seen, not on how it was cut into buffers.
		private long _samplesFed;
		private long _windowStart;
		private long _lastToneEnd = -1;

		/// <param name="hangMs">
		/// Keep the squelch open this long after the tone was last seen. The tone's share of the window's energy falls on loud
		/// speech, so without a hang the gate chopped exactly the strongest syllables.
		/// </param>
		public CtcssDetector(double frequencyHz, double minStrength, int sampleRate, int windowMs = 200, int hangMs = 0)
		{
			_frequency = frequencyHz;
			_minStrength = minStrength;
			_sampleRate = sampleRate;
			_windowSamples = Math.Max(sampleRate * windowMs / 1000, sampleRate / 20);
			_hangMs = Math.Max(0, hangMs);
		}

		/// <summary>Whether the tone was present in the most recent analysis window.</summary>
		public bool IsPresent => _present;

		/// <summary>Whether the squelch is open: the tone is present, or was within the hang time.</summary>
		public bool IsOpen => _open;

		/// <summary>Feeds audio; returns whether the squelch is open (tone present, or still inside the hang after it).</summary>
		public bool Process(ReadOnlySpan<short> pcm)
		{
			if (_frequency <= 0)
			{
				_present = false;
				_open = false;
				return false;
			}

			for (int i = 0; i < pcm.Length; i++)
				_window.Add(pcm[i]);
			_samplesFed += pcm.Length;

			while (_window.Count >= _windowSamples)
			{
				var block = _window.GetRange(0, _windowSamples).ToArray();
				var strength = Goertzel.NormalizedStrength(block, _frequency, _sampleRate);
				_present = strength >= _minStrength;
				if (_present)
					_lastToneEnd = _windowStart + _windowSamples;

				var hop = _windowSamples / 2; // 50% overlap for responsiveness
				_window.RemoveRange(0, hop);
				_windowStart += hop;
			}

			// Open while the latest window holds the tone, or within the hang of the last one that did.
			_open = _present || (_lastToneEnd >= 0 && (_samplesFed - _lastToneEnd) * 1000.0 / _sampleRate < _hangMs);
			return _open;
		}

		/// <summary>Closes the squelch and discards the buffered analysis window.</summary>
		public void Reset()
		{
			_window.Clear();
			_present = false;
			_open = false;
			_windowStart = _samplesFed;
			_lastToneEnd = -1;
		}
	}
}
