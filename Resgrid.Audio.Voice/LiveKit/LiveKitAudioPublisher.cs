using System;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Rtc;
using Resgrid.Audio.Voice.Abstractions;
using Serilog;

namespace Resgrid.Audio.Voice.LiveKit
{
	/// <summary>
	/// Feeds PCM16 mono 48 kHz audio into a published LiveKit audio track. Accepts
	/// arbitrary-length writes and emits fixed 10 ms (480-sample) frames, buffering
	/// any residual between calls. Backed by a LiveKit <see cref="AudioSource"/>.
	/// </summary>
	internal sealed class LiveKitAudioPublisher : IAudioPublisher
	{
		private readonly AudioSource _source;
		private readonly LocalAudioTrack _track;
		private readonly LocalParticipant _participant;
		private readonly ILogger _logger;
		private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

		private readonly short[] _residual = new short[AudioFormat.SamplesPerFrame];
		private int _residualCount;
		private bool _disposed;

		public LiveKitAudioPublisher(AudioSource source, LocalAudioTrack track, LocalParticipant participant, ILogger logger)
		{
			_source = source;
			_track = track;
			_participant = participant;
			_logger = logger;
		}

		public async ValueTask WriteAsync(ReadOnlyMemory<short> pcm48kMono, CancellationToken cancellationToken = default)
		{
			if (_disposed)
				return;

			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				// Work over ReadOnlyMemory; take spans only inside synchronous copies so
				// nothing span-typed is held across an await boundary.
				var input = pcm48kMono;
				int len = input.Length;
				int offset = 0;

				// Top up an in-progress residual frame first.
				if (_residualCount > 0)
				{
					int need = AudioFormat.SamplesPerFrame - _residualCount;
					int take = Math.Min(need, len);
					input.Slice(offset, take).Span.CopyTo(_residual.AsSpan(_residualCount));
					_residualCount += take;
					offset += take;

					if (_residualCount == AudioFormat.SamplesPerFrame)
					{
						var frame = new short[AudioFormat.SamplesPerFrame];
						Array.Copy(_residual, frame, AudioFormat.SamplesPerFrame);
						await CaptureAsync(frame, cancellationToken).ConfigureAwait(false);
						_residualCount = 0;
					}
				}

				// Emit whole frames straight from the input.
				while (len - offset >= AudioFormat.SamplesPerFrame)
				{
					var frame = new short[AudioFormat.SamplesPerFrame];
					input.Slice(offset, AudioFormat.SamplesPerFrame).Span.CopyTo(frame);
					await CaptureAsync(frame, cancellationToken).ConfigureAwait(false);
					offset += AudioFormat.SamplesPerFrame;
				}

				// Stash the remainder.
				int remaining = len - offset;
				if (remaining > 0)
				{
					input.Slice(offset, remaining).Span.CopyTo(_residual.AsSpan(0));
					_residualCount = remaining;
				}
			}
			finally
			{
				_gate.Release();
			}
		}

		public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
		{
			if (_disposed)
				return;

			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				if (_residualCount > 0)
				{
					var frame = new short[AudioFormat.SamplesPerFrame];
					Array.Copy(_residual, frame, _residualCount); // remainder zero-padded (silence)
					await CaptureAsync(frame, cancellationToken).ConfigureAwait(false);
					_residualCount = 0;
				}
			}
			finally
			{
				_gate.Release();
			}
		}

		private async Task CaptureAsync(short[] frameSamples, CancellationToken cancellationToken)
		{
			var frame = new AudioFrame(frameSamples, AudioFormat.SampleRate, AudioFormat.Channels, AudioFormat.SamplesPerFrame, null);
			await _source.CaptureFrameAsync(frame, cancellationToken).ConfigureAwait(false);
		}

		public async ValueTask DisposeAsync()
		{
			if (_disposed)
				return;

			// Flush buffered residual audio BEFORE marking disposed — FlushAsync
			// short-circuits once _disposed is set, which would drop the final partial frame.
			try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
			catch (Exception ex) { _logger?.Debug(ex, "Publisher flush on dispose failed"); }

			_disposed = true;

			try
			{
				if (_track != null && _participant != null)
					await _participant.UnpublishTrackAsync(_track.Sid, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex) { _logger?.Debug(ex, "Unpublish on dispose failed"); }

			try { _source?.Dispose(); }
			catch (Exception ex) { _logger?.Debug(ex, "AudioSource dispose failed"); }

			_gate.Dispose();
		}
	}
}
