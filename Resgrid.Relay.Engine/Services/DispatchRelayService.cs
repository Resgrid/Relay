using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Voice;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// 'dispatch' relay service: tones out new Resgrid calls (alert tones + TTS) onto a
	/// PTT channel. Cross-platform.
	/// </summary>
	public sealed class DispatchRelayService : RelayServiceBase
	{
		public DispatchRelayService(RelayHostOptions options, ILogger logger)
			: base("dispatch", options, logger)
		{
		}

		protected override bool IsLiveKitMode => true;

		protected override async Task ExecuteAsync(CancellationToken token)
		{
			MutableStatus.LiveKit = ConnectionState.Connecting;
			MutableStatus.Tts = ConnectionState.Connecting;
			ThrowIfFailed(await DispatchVoiceMode.RunAsync(Options, Logger, token, MutableStatus).ConfigureAwait(false));
		}
	}
}
