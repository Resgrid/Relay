using System.Collections.Generic;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Audio.Voice.Recording;
using Resgrid.Audio.Voice.ToneOut;

namespace Resgrid.Relay.Engine.Configuration
{
	// Option POCOs for the LiveKit voice modes (radio / record / dispatch). These are
	// cross-platform — they reference only the Resgrid.Audio.Voice library, never the
	// Windows-only Resgrid.Audio.Core. The radio mode maps these into Core's
	// RadioSettings inside the Windows-only build.

	/// <summary>How the relay selects and connects to a PTT channel (LiveKit room).</summary>
	public sealed class VoiceConnectionOptions
	{
		/// <summary>Department id to act on behalf of (hosted/system-key). Empty = authenticated department.</summary>
		public string DepartmentId { get; set; } = "";

		/// <summary>Channel selector: a channel id, channel name, or "default".</summary>
		public string Channel { get; set; } = "default";

		/// <summary>LiveKit publish queue depth in ms (publisher back-pressure buffer).</summary>
		public int PublishQueueMs { get; set; } = 1000;

		/// <summary>Honor the department's concurrent-seat subscription limit before joining.</summary>
		public bool EnforceSeatLimit { get; set; } = true;
	}

	/// <summary>Emergency/MDC-1200 signaling detection options (radio mode).</summary>
	public sealed class EmergencyOptions
	{
		public bool DetectMdc1200 { get; set; }
		public bool DetectTones { get; set; }

		/// <summary>Emergency tone frequencies (Hz) to listen for.</summary>
		public List<double> ToneFrequencies { get; set; } = new List<double>();

		/// <summary>Create a high-priority Resgrid call when an emergency is detected.</summary>
		public bool CreateCall { get; set; }

		public int CallPriority { get; set; } = 1;

		/// <summary>Optional DispatchList for the emergency call (e.g. "G:42").</summary>
		public string DispatchList { get; set; } = "";

		/// <summary>
		/// Repeats of the same emergency (same source and unit/tone) inside this window create no further calls; radios resend
		/// MDC-1200 emergency packets until acknowledged.
		/// </summary>
		public int CooldownSeconds { get; set; } = 120;
	}

	/// <summary>Physical radio interface options (radio mode, Windows).</summary>
	public sealed class RadioModeOptions
	{
		/// <summary>
		/// The <see cref="RadioProfiles"/> preset these settings came from (custom | analog | rapidradios | digatalkplus | poc).
		/// Informational: the values below are what runs; the profile only drives setup advice and start-up warnings.
		/// </summary>
		public string Profile { get; set; } = RadioProfiles.CustomKey;

		public int InputDevice { get; set; }
		public int OutputDevice { get; set; } = -1;

		/// <summary>PTT keying: Vox | SerialRts | SerialDtr | Cm108.</summary>
		public string PttMethod { get; set; } = "Vox";

		public string SerialPort { get; set; } = "";

		/// <summary>USB vendor id of the CM108 PTT interface: 0x0D8C (3468) for C-Media fobs, 0x1209 (4617) for the AIOC.</summary>
		public int Cm108VendorId { get; set; } = 0x0D8C;

		/// <summary>
		/// USB product id of the CM108 PTT interface: 0 for any product of the vendor (for 0x1209, only the AIOC), 0x7388
		/// (29576) for the AIOC.
		/// </summary>
		public int Cm108ProductId { get; set; } = 0;
		public int Cm108GpioPin { get; set; } = 3;

		/// <summary>Hardware carrier detect source: None | SerialCts | SerialDsr | Cm108Gpio.</summary>
		public string CarrierDetect { get; set; } = "None";
		public bool CarrierDetectInverted { get; set; }

		public bool CourtesyTone { get; set; } = true;

		/// <summary>
		/// Milliseconds between keying PTT and the first channel audio (0–3000). Set 500–800 for PoC/LTE radios that wait
		/// for a floor grant before passing mic audio. Ignored with Vox keying.
		/// </summary>
		public int TxLeadMs { get; set; } = 0;

		public int TxHangMs { get; set; } = 300;
		public int TxTailMs { get; set; } = 150;

		/// <summary>
		/// Transmit time-out in seconds (0 = no limit): a channel talker holding the radio keyed longer than this is cut off and
		/// ignored until they stop talking.
		/// </summary>
		public int MaxTransmitSeconds { get; set; } = 180;

		public bool AntiLoop { get; set; } = true;
		public double TxGain { get; set; } = 1.0;
		public double RxGain { get; set; } = 1.0;
		public int HighPassCutoffHz { get; set; } = 250;

		/// <summary>Receive-side squelch / anti-static settings.</summary>
		public SquelchSettings Squelch { get; set; } = new SquelchSettings();

		/// <summary>Also record the channel to disk/S3 while bridging.</summary>
		public bool RecordWhileBridging { get; set; }

		public EmergencyOptions Emergency { get; set; } = new EmergencyOptions();
	}

	/// <summary>S3-compatible object storage options for recordings.</summary>
	public sealed class S3StorageOptions
	{
		public string Endpoint { get; set; } = "";
		public string AccessKey { get; set; } = "";
		public string SecretKey { get; set; } = "";
		public string Region { get; set; } = "";
		public string Bucket { get; set; } = "";
		public string Prefix { get; set; } = "relay-recordings";
		public bool ForcePathStyle { get; set; }
		public bool UseSsl { get; set; } = true;
	}

	/// <summary>Compliance recorder options (record mode, or while bridging).</summary>
	public sealed class RecorderModeOptions
	{
		/// <summary>Channel selector, or "all" to record every channel in the department.</summary>
		public string Channel { get; set; } = "all";
		public string DepartmentId { get; set; } = "";

		/// <summary>Audio storage: local | s3 | both.</summary>
		public string Store { get; set; } = "local";
		public string LocalPath { get; set; } = "";

		/// <summary>Metadata log: jsonl | sqlite | none.</summary>
		public string Log { get; set; } = "jsonl";
		public string LogPath { get; set; } = "";

		public S3StorageOptions S3 { get; set; } = new S3StorageOptions();

		/// <summary>Transmission segmentation tunables.</summary>
		public RecorderSettings Segmentation { get; set; } = new RecorderSettings();
	}

	/// <summary>Dispatch tone-out options (dispatch mode).</summary>
	public sealed class DispatchVoiceOptions
	{
		public string DepartmentId { get; set; } = "";
		public string Channel { get; set; } = "default";

		/// <summary>How often to poll for new calls to announce.</summary>
		public int PollSeconds { get; set; } = 15;

		/// <summary>How long a call's tone-out is retried (every poll) before the relay gives up on it and logs an error.</summary>
		public int GiveUpAfterMinutes { get; set; } = 10;

		/// <summary>Alert tone profile played before the spoken announcement.</summary>
		public ToneProfile Tone { get; set; } = new ToneProfile();

		/// <summary>Hosted (multi-department staff) mode — designed-for, deferred.</summary>
		public bool Hosted { get; set; }
	}
}
