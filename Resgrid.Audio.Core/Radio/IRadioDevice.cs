using System;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>
	/// Abstraction over the physical radio's audio in/out (receive audio capture and
	/// transmit audio playback), at the engine rate (48 kHz mono PCM16).
	/// </summary>
	public interface IRadioDevice : IDisposable
	{
		/// <summary>Raised with PCM16 mono 48 kHz frames captured from the radio receiver.</summary>
		event EventHandler<short[]> SamplesReceived;

		void StartReceive();
		void StopReceive();

		/// <summary>Begins/holds the transmit audio path open (call before keying PTT).</summary>
		void StartTransmit();

		/// <summary>Queues PCM16 mono 48 kHz samples to play out to the radio's mic input.</summary>
		void Transmit(short[] pcm48kMono);

		/// <summary>Stops the transmit audio path.</summary>
		void StopTransmit();
	}
}
