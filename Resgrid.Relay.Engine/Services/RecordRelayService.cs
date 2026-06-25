using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Voice;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// 'record' relay service: records every PTT transmission on one or all channels.
	/// Cross-platform.
	/// </summary>
	public sealed class RecordRelayService : RelayServiceBase
	{
		public RecordRelayService(RelayHostOptions options, ILogger logger)
			: base("record", options, logger)
		{
		}

		protected override async Task ExecuteAsync(CancellationToken token)
		{
			MutableStatus.LiveKit = ConnectionState.Connecting;
			await RecordMode.RunAsync(Options, Logger, token, MutableStatus).ConfigureAwait(false);
		}
	}
}
