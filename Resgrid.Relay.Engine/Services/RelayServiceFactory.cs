using System;
using Resgrid.Relay.Engine.Configuration;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// Creates the right <see cref="IRelayService"/> for a configured mode. Windows-only
	/// modes (radio/audio) resolve to their real service on a Windows build and to a
	/// <see cref="NotSupportedRelayService"/> elsewhere so the host can still surface a
	/// clear Faulted state instead of crashing.
	/// </summary>
	public static class RelayServiceFactory
	{
		public static IRelayService Create(RelayHostOptions options, ILogger logger)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			var mode = string.IsNullOrWhiteSpace(options.Mode) ? "smtp" : options.Mode.Trim().ToLowerInvariant();

			switch (mode)
			{
				case "smtp":
					return new SmtpRelayService(options, logger);
				case "record":
					return new RecordRelayService(options, logger);
				case "dispatch":
					return new DispatchRelayService(options, logger);
				case "audio":
#if NET10_0_WINDOWS
					return new AudioImportService(options, logger);
#else
					return new NotSupportedRelayService(mode, options, logger);
#endif
				case "radio":
#if NET10_0_WINDOWS
					return new RadioRelayService(options, logger);
#else
					return new NotSupportedRelayService(mode, options, logger);
#endif
				default:
					throw new ArgumentException(
						$"Unsupported relay mode '{options.Mode}'. Supported modes are 'smtp', 'audio', 'radio', 'record' and 'dispatch'.",
						nameof(options));
			}
		}
	}
}
