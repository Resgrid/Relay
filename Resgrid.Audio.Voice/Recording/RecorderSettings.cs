namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Tunables controlling how a channel's audio is segmented into discrete
	/// transmissions for recording.
	/// </summary>
	public sealed class RecorderSettings
	{
		/// <summary>End a transmission after this many ms of silence (no active audio).</summary>
		public int HangMs { get; set; } = 1500;

		/// <summary>Hard cap on a single transmission; rolls to a new file beyond this.</summary>
		public int MaxSegmentSeconds { get; set; } = 300;

		/// <summary>Drop transmissions whose total recorded audio is shorter than this.</summary>
		public int MinActiveMs { get; set; } = 250;

		/// <summary>dBFS below which a frame is treated as silence for segmentation.</summary>
		public double SilenceFloorDbfs { get; set; } = -50;

		/// <summary>How often the finalizer scans for ended transmissions.</summary>
		public int ScanIntervalMs { get; set; } = 500;
	}
}
