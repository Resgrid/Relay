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
		// Survives the resilience layer's restarts of ExecuteAsync, so a reconnect resumes instead of re-priming the backlog.
		private readonly DispatchToneOutState _toneOutState = new DispatchToneOutState();

		public DispatchRelayService(RelayHostOptions options, ILogger logger)
			: base("dispatch", options, logger)
		{
		}

		protected override bool IsLiveKitMode => true;

		protected override void ValidateConfiguration() => ValidateApiOptions(Options.Resgrid);

		protected override async Task ExecuteAsync(CancellationToken token)
		{
			MutableStatus.LiveKit = ConnectionState.Connecting;
			MutableStatus.Tts = ConnectionState.Connecting;
			ThrowIfFailed(await DispatchVoiceMode.RunAsync(Options, Logger, token, MutableStatus, _toneOutState).ConfigureAwait(false));
		}
	}
}
