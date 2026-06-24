namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>The style of alert tones played before a spoken dispatch announcement.</summary>
	public enum ToneType
	{
		/// <summary>No alert tones; speak immediately.</summary>
		None = 0,

		/// <summary>Two-tone sequential paging (QCII): tone A then tone B.</summary>
		TwoToneSequential = 1,

		/// <summary>Alternating high/low "warble" attention signal.</summary>
		HiLo = 2,

		/// <summary>A single steady attention tone.</summary>
		SingleTone = 3
	}

	/// <summary>
	/// Defines the alert tones prepended to a dispatch tone-out so personnel are
	/// alerted before the spoken call details — mirroring fire/EMS station paging.
	/// </summary>
	public sealed class ToneProfile
	{
		public ToneType Type { get; set; } = ToneType.TwoToneSequential;

		/// <summary>Tone A frequency (Hz). For HiLo this is the high tone.</summary>
		public double ToneAFrequency { get; set; } = 1000;

		/// <summary>Tone A duration (ms). For TwoToneSequential the "A" tone is short.</summary>
		public int ToneADurationMs { get; set; } = 1000;

		/// <summary>Tone B frequency (Hz). For HiLo this is the low tone.</summary>
		public double ToneBFrequency { get; set; } = 2000;

		/// <summary>Tone B duration (ms). For TwoToneSequential the "B" tone is long.</summary>
		public int ToneBDurationMs { get; set; } = 3000;

		/// <summary>Output amplitude 0..1.</summary>
		public double Amplitude { get; set; } = 0.5;

		/// <summary>For HiLo: number of high/low alternations.</summary>
		public int HiLoCycles { get; set; } = 6;

		/// <summary>For HiLo: duration of each high or low segment (ms).</summary>
		public int HiLoSegmentMs { get; set; } = 250;

		/// <summary>Silence inserted between the tones and the spoken announcement (ms).</summary>
		public int PreSpeechSilenceMs { get; set; } = 500;
	}
}
