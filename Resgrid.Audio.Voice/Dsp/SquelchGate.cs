using System;
using Resgrid.Audio.Voice.Abstractions;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>How a radio receive path decides that real traffic (not static) is present.</summary>
	public enum SquelchMode
	{
		/// <summary>
		/// For a radio with its own (hardware) squelch, whose audio is silent when closed: relays any audio at or above
		/// <see cref="SquelchSettings.OpenDbfs"/>, held through pauses for <see cref="SquelchSettings.HangMs"/>; silence is not
		/// relayed and does not count as receiving.
		/// </summary>
		Off = 0,

		/// <summary>Audio-level (VOX) gate with hysteresis — the default anti-static gate.</summary>
		Vox = 1,

		/// <summary>Hardware carrier-operated relay (COR/COS) via serial/GPIO — gate handled by the device.</summary>
		Carrier = 2,

		/// <summary>Sub-audible CTCSS/PL tone must be present to open.</summary>
		Ctcss = 3
	}

	/// <summary>Tunables for the VOX/level squelch — the core "don't relay static" controls.</summary>
	public sealed class SquelchSettings
	{
		public SquelchMode Mode { get; set; } = SquelchMode.Vox;

		/// <summary>dBFS at/above which the gate opens. Set just above the static floor.</summary>
		public double OpenDbfs { get; set; } = -38;

		/// <summary>
		/// dBFS below which the hang runs down at full speed (must be ≤ OpenDbfs). Between this and OpenDbfs it runs down at half
		/// speed: only audio at or above OpenDbfs keeps the gate open indefinitely.
		/// </summary>
		public double CloseDbfs { get; set; } = -45;

		/// <summary>Keep the gate open this long after audio drops, to avoid chopping speech.</summary>
		public int HangMs { get; set; } = 600;

		/// <summary>CTCSS/PL tone frequency in Hz (Ctcss mode).</summary>
		public double CtcssFrequency { get; set; } = 0;

		/// <summary>Goertzel strength (0..1) required for CTCSS detection.</summary>
		public double CtcssMinStrength { get; set; } = 0.30;
	}

	/// <summary>
	/// Level-based (VOX) squelch with open/close hysteresis and a hang timer. Fed one
	/// frame at a time, it decides whether the radio receive audio is real traffic
	/// worth relaying or just noise/static. This is the primary tuning knob that keeps
	/// static off the Resgrid channel.
	/// </summary>
	public sealed class SquelchGate
	{
		private readonly SquelchSettings _settings;
		private readonly int _sampleRate;
		private bool _open;
		private double _hangRemainingMs;

		public SquelchGate(SquelchSettings settings, int sampleRate = AudioFormat.SampleRate)
		{
			_settings = settings ?? new SquelchSettings();
			_sampleRate = sampleRate;
		}

		public bool IsOpen => _open;

		/// <summary>Most recent measured frame level in dBFS (for live tuning meters).</summary>
		public double LastDbfs { get; private set; }

		/// <summary>Processes one frame; returns whether the gate is open (relay this audio).</summary>
		public bool Process(ReadOnlySpan<short> frame)
		{
			double db = AudioFormat.Dbfs(frame);
			LastDbfs = db;
			double frameMs = frame.Length * 1000.0 / _sampleRate;

			if (db >= _settings.OpenDbfs)
			{
				_open = true;
				_hangRemainingMs = _settings.HangMs;
			}
			else if (_open)
			{
				// Only real traffic (at/above OpenDbfs) re-arms the hang. Holding the gate open for anything between the two
				// thresholds kept it open forever once a transmission ended, because the documented tuning puts OpenDbfs just
				// above the static floor, which leaves the static itself inside that band.
				_hangRemainingMs -= db < _settings.CloseDbfs ? frameMs : frameMs / 2;
				if (_hangRemainingMs <= 0)
					_open = false;
			}

			return _open;
		}

		public void ForceClosed()
		{
			_open = false;
			_hangRemainingMs = 0;
		}
	}
}
