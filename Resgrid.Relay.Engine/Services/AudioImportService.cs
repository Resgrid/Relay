#if NET10_0_WINDOWS
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Voice;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// 'audio' relay service (Windows): tone-detect dispatch importer that watches a
	/// Windows audio device and creates Resgrid calls.
	/// </summary>
	public sealed class AudioImportService : RelayServiceBase
	{
		public AudioImportService(RelayHostOptions options, ILogger logger)
			: base("audio", options, logger)
		{
		}

		protected override async Task ExecuteAsync(CancellationToken token)
		{
			ThrowIfFailed(await AudioImportMode.RunAsync(Options, Logger, token, MutableStatus).ConfigureAwait(false));
		}
	}
}
#endif
