using System;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>Why the radio path stopped working (<see cref="IRadioDevice.Faulted"/>, <see cref="RadioBridge.Faulted"/>).</summary>
	public sealed class RadioFaultEventArgs : EventArgs
	{
		public RadioFaultEventArgs(string reason, Exception exception = null)
		{
			Reason = string.IsNullOrWhiteSpace(reason) ? "unknown radio fault" : reason;
			Exception = exception;
		}

		public string Reason { get; }
		public Exception Exception { get; }

		public override string ToString() => Exception == null ? Reason : $"{Reason}: {Exception.Message}";
	}

	/// <summary>
	/// Abstraction over the physical radio's audio in/out (receive audio capture and
	/// transmit audio playback), at the engine rate (48 kHz mono PCM16).
	/// </summary>
	public interface IRadioDevice : IDisposable
	{
		/// <summary>Raised with PCM16 mono 48 kHz frames captured from the radio receiver.</summary>
		event EventHandler<short[]> SamplesReceived;

		/// <summary>
		/// Raised when capture or playback stops without being asked to (a USB glitch, the interface being unplugged, a
		/// driver that stops delivering audio). The device is then deaf and/or mute until it is reopened, so the owner must
		/// rebuild it rather than keep keying the radio into a dead transmit path.
		/// </summary>
		event EventHandler<RadioFaultEventArgs> Faulted;

		void StartReceive();
		void StopReceive();

		/// <summary>Begins/holds the transmit audio path open (call before keying PTT).</summary>
		void StartTransmit();

		/// <summary>Queues PCM16 mono 48 kHz samples to play out to the radio's mic input.</summary>
		void Transmit(short[] pcm48kMono);

		/// <summary>Stops the transmit audio path.</summary>
		void StopTransmit();

		/// <summary>
		/// Transmit audio queued but not yet handed to the output device. <see cref="TimeSpan.Zero"/> when the queue is empty
		/// or the device cannot tell. The output plays continuously (silence when the queue is empty), so a non-zero backlog
		/// must shrink: <see cref="RadioBridge"/> treats one that has stopped shrinking as a stalled output and faults.
		/// </summary>
		TimeSpan BufferedTransmit { get; }

		/// <summary>How long audio takes to play out once it leaves the queue (the output device's own buffering).</summary>
		TimeSpan OutputLatency { get; }
	}
}
