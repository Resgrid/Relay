using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Voice.Abstractions
{
	/// <summary>
	/// Publishes locally-sourced PCM16 mono 48 kHz audio into a PTT channel. Used by
	/// the radio bridge (radio RX → channel) and dispatch tone-out (tones + TTS →
	/// channel). Implementations chunk arbitrary-length input into 10 ms frames.
	/// </summary>
	public interface IAudioPublisher : IAsyncDisposable
	{
		/// <summary>
		/// Queues PCM16 mono 48 kHz samples for transmission. Any length is accepted;
		/// the publisher buffers a residual partial frame between calls.
		/// </summary>
		ValueTask WriteAsync(ReadOnlyMemory<short> pcm48kMono, CancellationToken cancellationToken = default);

		/// <summary>Pushes any buffered partial frame, padded with silence.</summary>
		ValueTask FlushAsync(CancellationToken cancellationToken = default);
	}
}
