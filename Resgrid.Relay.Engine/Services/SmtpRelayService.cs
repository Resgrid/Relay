using System;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Smtp;
using Resgrid.Providers.ApiClient.V4;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// 'smtp' relay service: runs the SMTP dispatch relay and projects API/Redis/SMTP
	/// connection state and the processed-message counter onto the status surface.
	/// Cross-platform.
	/// </summary>
	public sealed class SmtpRelayService : RelayServiceBase
	{
		public SmtpRelayService(RelayHostOptions options, ILogger logger)
			: base("smtp", options, logger)
		{
		}

		protected override async Task ExecuteAsync(CancellationToken token)
		{
			using var apiClient = new ResgridV4ApiClient(Options.Resgrid);

			MutableStatus.ResgridApi = ConnectionState.Connecting;
			try
			{
				var healthy = await apiClient.IsHealthyAsync(token).ConfigureAwait(false);
				MutableStatus.ResgridApi = healthy ? ConnectionState.Connected : ConnectionState.Disconnected;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// A failed health probe must not prevent the relay from listening — the
				// SMTP server can still accept mail and call the API per-message.
				Logger?.Warning(ex, "Resgrid API health probe failed at SMTP relay startup");
				MutableStatus.ResgridApi = ConnectionState.Disconnected;
			}

			// Redis is used lazily by the dispatch-lookup cache during message processing, so there
			// is no startup connection to probe here. Report it as applicable-but-unprobed (Unknown)
			// rather than a transitional "Connecting" that would never resolve.
			MutableStatus.Redis = Options.Smtp.RedisCache?.Enabled == true
				? ConnectionState.Unknown
				: ConnectionState.NotApplicable;

			var inner = SmtpTelemetry.Create(Options, Logger);
			await using var telemetry = new StatusReportingSmtpTelemetry(inner, MutableStatus);

			await SmtpRelayRunner.RunAsync(Options.Smtp, telemetry, apiClient, token).ConfigureAwait(false);
		}
	}
}
