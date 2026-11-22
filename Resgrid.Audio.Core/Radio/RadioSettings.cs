using Resgrid.Audio.Voice.Dsp;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>How the relay keys the radio's PTT (transmit) line.</summary>
	public enum PttKeyingMethod
	{
		/// <summary>No keying line — rely on the radio interface's own VOX (e.g. SignaLink).</summary>
		Vox = 0,

		/// <summary>Assert RTS on a serial/USB-serial port (CH340/FTDI, RIM, homebrew cables).</summary>
		SerialRts = 1,

		/// <summary>Assert DTR on a serial/USB-serial port.</summary>
		SerialDtr = 2,

		/// <summary>Toggle a CM108/CM119 USB sound-fob GPIO (DMK URI, RA-series, RIM-Lite).</summary>
		Cm108 = 3
	}

	/// <summary>Where the relay reads carrier-detect (COR/COS) when squelch mode is Carrier.</summary>
	public enum CarrierDetectSource
	{
		None = 0,
		SerialCts = 1,
		SerialDsr = 2,
		Cm108Gpio = 3
	}

	/// <summary>
	/// Configuration for the physical radio interface: audio devices, PTT keying,
	/// carrier detect, and the transmit-side tuning (gain, hang/tail, courtesy tone,
	/// anti-loop). The receive-side anti-static tuning lives in <see cref="Squelch"/>.
	/// </summary>
	public sealed class RadioSettings
	{
		/// <summary>WaveIn device index for radio receive audio.</summary>
		public int InputDevice { get; set; }

		/// <summary>WaveOut device index for radio transmit audio (-1 = system default).</summary>
		public int OutputDevice { get; set; } = -1;

		public PttKeyingMethod Ptt { get; set; } = PttKeyingMethod.Vox;

		/// <summary>Serial port name (COM3, /dev/ttyUSB0) for serial PTT and/or carrier detect.</summary>
		public string SerialPort { get; set; } = "";

		/// <summary>CM108 USB device VID (default C-Media 0x0D8C).</summary>
		public int Cm108VendorId { get; set; } = 0x0D8C;

		/// <summary>CM108 USB device PID (0 = match any C-Media device).</summary>
		public int Cm108ProductId { get; set; } = 0;

		/// <summary>CM108 GPIO pin used for PTT (1–4; URI/RIM commonly use GPIO3).</summary>
		public int Cm108GpioPin { get; set; } = 3;

		public CarrierDetectSource CarrierDetect { get; set; } = CarrierDetectSource.None;

		/// <summary>Invert the carrier-detect polarity (some interfaces are active-low).</summary>
		public bool CarrierDetectInverted { get; set; }

		/// <summary>Play a short courtesy beep on the channel when the radio unkeys.</summary>
		public bool CourtesyTone { get; set; } = true;

		/// <summary>
		/// Delay between keying PTT and playing the first channel audio to the radio (0 = none, max 3000). Radios that need
		/// time after keying before they pass mic audio — push-to-talk-over-cellular (PoC/LTE) radios waiting for the server
		/// to grant the floor, or slow-keying transmitters — otherwise cut off the first words. Channel audio is delayed by
		/// this much for the whole transmission, and the unkey tail is extended to match. Ignored with Vox keying (silence
		/// cannot key a VOX interface, so it would only add delay).
		/// </summary>
		public int TxLeadMs { get; set; } = 0;

		/// <summary>Keep PTT keyed this long after channel audio stops (avoids choppy tails).</summary>
		public int TxHangMs { get; set; } = 300;

		/// <summary>Extra dead-carrier time after audio before dropping PTT.</summary>
		public int TxTailMs { get; set; } = 150;

		/// <summary>
		/// Transmit time-out (seconds; 0 = no limit). A channel talker who holds the radio keyed longer than this — a stuck
		/// mic, an open hot mic — is cut off: PTT drops and their audio is ignored until they go quiet, so the relay cannot sit
		/// on the frequency indefinitely.
		/// </summary>
		public int MaxTransmitSeconds { get; set; } = 180;

		/// <summary>Prevent relaying audio back to the side it came from (half-duplex arbitration).</summary>
		public bool AntiLoop { get; set; } = true;

		/// <summary>Linear gain applied to audio transmitted to the radio.</summary>
		public double TxGain { get; set; } = 1.0;

		/// <summary>Linear gain applied to received radio audio before publishing.</summary>
		public double RxGain { get; set; } = 1.0;

		/// <summary>High-pass cutoff (Hz) applied to received audio to strip sub-audible tones/hum.</summary>
		public int HighPassCutoffHz { get; set; } = 250;

		/// <summary>Receive-side squelch / anti-static settings.</summary>
		public SquelchSettings Squelch { get; set; } = new SquelchSettings();
	}
}
