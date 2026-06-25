#if NET10_0_WINDOWS
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Voice;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// 'radio' relay service (Windows): bridges a physically-attached radio with a
	/// Resgrid PTT channel.
	/// </summary>
	public sealed class RadioRelayService : RelayServiceBase
	{
		public RadioRelayService(RelayHostOptions options, ILogger logger)
			: base("radio", options, logger)
		{
		}

		protected override async Task ExecuteAsync(CancellationToken token)
		{
			MutableStatus.LiveKit = ConnectionState.Connecting;
			await RadioMode.RunAsync(Options, Logger, token, MutableStatus).ConfigureAwait(false);
		}
	}
}
#endif
