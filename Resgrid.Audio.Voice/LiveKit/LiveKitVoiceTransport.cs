using Resgrid.Audio.Voice.Abstractions;
using Serilog;

namespace Resgrid.Audio.Voice.LiveKit
{
	/// <summary>
	/// Creates LiveKit-backed voice sessions. The single composition root that binds
	/// the transport-agnostic engine to the LiveKit RTC SDK.
	/// </summary>
	public sealed class LiveKitVoiceTransport : IVoiceTransport
	{
		private readonly int _publishQueueMs;
		private readonly ILogger _logger;

		public LiveKitVoiceTransport(ILogger logger, int publishQueueMs = 1000)
		{
			_logger = logger;
			_publishQueueMs = publishQueueMs;
		}

		public IVoiceRoomSession CreateSession(VoiceChannel channel) =>
			new LiveKitRoomSession(channel, _publishQueueMs, _logger);
	}
}
