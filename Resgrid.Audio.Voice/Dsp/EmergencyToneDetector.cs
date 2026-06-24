using System;
using System.Collections.Generic;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>Tunables for sustained emergency-tone detection.</summary>
	public sealed class EmergencyToneSettings
	{
		/// <summary>Tone frequencies (Hz) that signal an emergency on this radio system.</summary>
		public List<double> Frequencies { get; set; } = new List<double>();

		/// <summary>Goertzel normalized strength (0..1) required to count a tone present.</summary>
		public double MinStrength { get; set; } = 0.35;

		/// <summary>Continuous milliseconds the tone must hold to trigger.</summary>
		public int HoldMs { get; set; } = 600;

		/// <summary>Suppress repeat triggers for this long after a detection.</summary>
		public int CooldownMs { get; set; } = 8000;

		/// <summary>Analysis block size in ms.</summary>
		public int BlockMs { get; set; } = 40;
	}

	/// <summary>A detected emergency signal.</summary>
	public sealed class EmergencyDetection
	{
		public EmergencyDetection(string kind, string detail, DateTime utc)
		{
			Kind = kind;
			Detail = detail;
			Utc = utc;
		}

		public string Kind { get; }
		public string Detail { get; }
		public DateTime Utc { get; }
	}

	/// <summary>
	/// Streaming detector for sustained emergency tones on the RF side. Many systems
	/// signal alarms with a held tone or warble; this raises an event when a configured
	/// frequency holds above threshold long enough, with a cooldown to avoid spamming.
	/// Feed it PCM16 mono frames at the engine rate.
	/// </summary>
	public sealed class EmergencyToneDetector
	{
		private readonly EmergencyToneSettings _settings;
		private readonly int _sampleRate;
		private readonly int _blockSamples;
		private readonly List<short> _window = new List<short>();

		private double _heldMs;
		private double _cooldownRemainingMs;
		private double _activeFrequency;

		public event EventHandler<EmergencyDetection> EmergencyDetected;

		public EmergencyToneDetector(EmergencyToneSettings settings, int sampleRate)
		{
			_settings = settings ?? new EmergencyToneSettings();
			_sampleRate = sampleRate;
			_blockSamples = Math.Max(64, _sampleRate * Math.Max(10, _settings.BlockMs) / 1000);
		}

		public void Process(ReadOnlySpan<short> pcm)
		{
			if (_settings.Frequencies == null || _settings.Frequencies.Count == 0)
				return;

			for (int i = 0; i < pcm.Length; i++)
				_window.Add(pcm[i]);

			while (_window.Count >= _blockSamples)
			{
				var block = _window.GetRange(0, _blockSamples).ToArray();
				_window.RemoveRange(0, _blockSamples);
				AnalyzeBlock(block);
			}
		}

		private void AnalyzeBlock(short[] block)
		{
			double blockMs = block.Length * 1000.0 / _sampleRate;

			if (_cooldownRemainingMs > 0)
			{
				_cooldownRemainingMs -= blockMs;
				return;
			}

			double bestFreq = 0;
			double bestStrength = 0;
			foreach (var f in _settings.Frequencies)
			{
				var strength = Goertzel.NormalizedStrength(block, f, _sampleRate);
				if (strength > bestStrength)
				{
					bestStrength = strength;
					bestFreq = f;
				}
			}

			bool present = bestStrength >= _settings.MinStrength;
			if (present && (Math.Abs(bestFreq - _activeFrequency) < 1 || _heldMs == 0))
			{
				_activeFrequency = bestFreq;
				_heldMs += blockMs;
				if (_heldMs >= _settings.HoldMs)
				{
					EmergencyDetected?.Invoke(this, new EmergencyDetection(
						"Tone", $"Emergency tone {_activeFrequency:0} Hz held {_heldMs:0} ms", DateTime.UtcNow));
					_heldMs = 0;
					_cooldownRemainingMs = _settings.CooldownMs;
				}
			}
			else
			{
				_heldMs = 0;
				_activeFrequency = 0;
			}
		}
	}
}
