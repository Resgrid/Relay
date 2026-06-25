using System;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// Placeholder relay service returned for Windows-only modes (radio/audio) when the
	/// engine is running on a non-Windows build. Faults immediately with a clear message;
	/// <see cref="RelayServiceBase"/> catches the exception and transitions to Faulted.
	/// </summary>
	public sealed class NotSupportedRelayService : RelayServiceBase
	{
		public NotSupportedRelayService(string mode, RelayHostOptions options, ILogger logger)
			: base(mode, options, logger)
		{
		}

		protected override Task ExecuteAsync(CancellationToken token)
		{
			throw new PlatformNotSupportedException($"The '{Mode}' relay mode requires Windows.");
		}
	}
}
