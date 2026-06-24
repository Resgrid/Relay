namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// Connection settings for the Resgrid Core TTS microservice
	/// (POST /tts, GET /tts/audio/{hash}.wav). Matches Resgrid's TtsConfig.
	/// </summary>
	public sealed class TtsSettings
	{
		/// <summary>Base URL of the TTS service (e.g. https://tts.resgrid.com).</summary>
		public string ServiceBaseUrl { get; set; } = "";

		/// <summary>Optional separate base URL for audio playback/download.</summary>
		public string PlaybackBaseUrl { get; set; } = "";

		/// <summary>Piper voice id, e.g. "en-us+klatt4". Empty = service default.</summary>
		public string Voice { get; set; } = "";

		/// <summary>Words-per-minute speed (80–450). 0 = service default.</summary>
		public int Speed { get; set; }

		/// <summary>HTTP timeout for synthesis requests.</summary>
		public int RequestTimeoutSeconds { get; set; } = 20;
	}
}
