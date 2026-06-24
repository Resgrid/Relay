using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Audio;
using Serilog;

namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// <see cref="ITextToSpeech"/> backed by the Resgrid Core TTS service. Requests
	/// synthesis (POST /tts), downloads the generated 8 kHz mono µ-law WAV
	/// (GET /tts/audio/{hash}.wav), and resamples it to 48 kHz PCM16 for publishing.
	/// </summary>
	public sealed class ResgridTtsClient : ITextToSpeech, IDisposable
	{
		private readonly TtsSettings _settings;
		private readonly HttpClient _http;
		private readonly ILogger _logger;

		public ResgridTtsClient(TtsSettings settings, ILogger logger, HttpClient http = null)
		{
			_settings = settings ?? throw new ArgumentNullException(nameof(settings));
			if (string.IsNullOrWhiteSpace(_settings.ServiceBaseUrl))
				throw new ArgumentException("Tts ServiceBaseUrl is required for dispatch tone-out.", nameof(settings));

			_logger = logger;
			_http = http ?? new HttpClient();
			_http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _settings.RequestTimeoutSeconds));
		}

		public async Task<short[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(text))
				return Array.Empty<short>();

			var request = new TtsRequest
			{
				Text = text,
				Voice = string.IsNullOrWhiteSpace(_settings.Voice) ? null : _settings.Voice,
				Speed = _settings.Speed > 0 ? _settings.Speed : (int?)null
			};

			var generateUrl = Combine(_settings.ServiceBaseUrl, "tts");
			using var postResponse = await _http.PostAsJsonAsync(generateUrl, request, cancellationToken).ConfigureAwait(false);
			postResponse.EnsureSuccessStatusCode();

			var result = await postResponse.Content.ReadFromJsonAsync<TtsResponse>(cancellationToken).ConfigureAwait(false);
			if (result == null || string.IsNullOrWhiteSpace(result.Hash))
				throw new InvalidOperationException("TTS service did not return an audio hash.");

			var audioUrl = !string.IsNullOrWhiteSpace(result.Url) && result.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
				? result.Url
				: Combine(PlaybackBase(), $"tts/audio/{result.Hash}.wav");

			var wav = await _http.GetByteArrayAsync(audioUrl, cancellationToken).ConfigureAwait(false);
			var (samples, sampleRate) = WavIo.ReadToPcm16Mono(wav);

			var resampled = Resampler.Resample(samples, sampleRate, AudioFormat.SampleRate);
			_logger?.Debug("Synthesized {Chars} chars -> {Ms} ms of audio (cached={Cached})",
				text.Length, resampled.Length / (AudioFormat.SampleRate / 1000), result.Cached);
			return resampled;
		}

		private string PlaybackBase() =>
			string.IsNullOrWhiteSpace(_settings.PlaybackBaseUrl) ? _settings.ServiceBaseUrl : _settings.PlaybackBaseUrl;

		private static string Combine(string baseUrl, string path) =>
			$"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

		public void Dispose() => _http?.Dispose();

		private sealed class TtsRequest
		{
			[JsonPropertyName("text")] public string Text { get; set; }
			[JsonPropertyName("voice")] public string Voice { get; set; }
			[JsonPropertyName("speed")] public int? Speed { get; set; }
		}

		private sealed class TtsResponse
		{
			[JsonPropertyName("hash")] public string Hash { get; set; }
			[JsonPropertyName("objectKey")] public string ObjectKey { get; set; }
			[JsonPropertyName("url")] public string Url { get; set; }
			[JsonPropertyName("voice")] public string Voice { get; set; }
			[JsonPropertyName("speed")] public int Speed { get; set; }
			[JsonPropertyName("cached")] public bool Cached { get; set; }
		}
	}
}
