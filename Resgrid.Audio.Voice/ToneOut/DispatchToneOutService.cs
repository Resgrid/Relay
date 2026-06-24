using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Serilog;

namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// Builds and publishes a dispatch "tone-out": alert tones followed by a spoken
	/// (TTS) announcement of the call, transmitted onto a PTT channel — the LiveKit
	/// equivalent of toning out a station over the radio.
	/// </summary>
	public sealed class DispatchToneOutService
	{
		private readonly ITextToSpeech _tts;
		private readonly ToneGenerator _toneGenerator;
		private readonly ToneProfile _profile;
		private readonly ILogger _logger;

		public DispatchToneOutService(ITextToSpeech tts, ToneGenerator toneGenerator, ToneProfile profile, ILogger logger)
		{
			_tts = tts ?? throw new ArgumentNullException(nameof(tts));
			_toneGenerator = toneGenerator ?? new ToneGenerator();
			_profile = profile ?? new ToneProfile();
			_logger = logger;
		}

		/// <summary>Renders alert tones + TTS for <paramref name="text"/> into one PCM16 48 kHz buffer.</summary>
		public async Task<short[]> BuildAnnouncementAsync(string text, CancellationToken cancellationToken = default)
		{
			var alert = _toneGenerator.BuildAlert(_profile);
			var speech = await _tts.SynthesizeAsync(text, cancellationToken).ConfigureAwait(false);

			var combined = new short[alert.Length + speech.Length];
			Array.Copy(alert, 0, combined, 0, alert.Length);
			Array.Copy(speech, 0, combined, alert.Length, speech.Length);
			return combined;
		}

		/// <summary>Builds and transmits an announcement on the given publisher.</summary>
		public async Task AnnounceAsync(IAudioPublisher publisher, string text, CancellationToken cancellationToken = default)
		{
			if (publisher == null)
				throw new ArgumentNullException(nameof(publisher));

			// Normalize once so the preview logging (text.Length/Substring) and the
			// announcement build can't NRE on a null text argument.
			text ??= string.Empty;

			var audio = await BuildAnnouncementAsync(text, cancellationToken).ConfigureAwait(false);
			_logger?.Information("Toning out dispatch ({Ms} ms): {Preview}",
				audio.Length / (AudioFormat.SampleRate / 1000),
				text.Length > 80 ? text.Substring(0, 80) + "…" : text);

			// CaptureFrameAsync back-pressures on the AudioSource queue, so this paces
			// roughly in real time as the audio is transmitted.
			await publisher.WriteAsync(audio, cancellationToken).ConfigureAwait(false);
			await publisher.FlushAsync(cancellationToken).ConfigureAwait(false);
		}

		/// <summary>Builds and transmits the announcement across several channels.</summary>
		public async Task AnnounceToChannelsAsync(IEnumerable<IAudioPublisher> publishers, string text, CancellationToken cancellationToken = default)
		{
			var audio = await BuildAnnouncementAsync(text, cancellationToken).ConfigureAwait(false);
			foreach (var publisher in publishers)
			{
				try
				{
					await publisher.WriteAsync(audio, cancellationToken).ConfigureAwait(false);
					await publisher.FlushAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// Normal shutdown — let cancellation propagate instead of logging it as a failure.
					throw;
				}
				catch (Exception ex)
				{
					_logger?.Error(ex, "Failed to tone out on a channel");
				}
			}
		}
	}
}
