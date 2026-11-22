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
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				// Only a stop request ends the service here. HttpClient reports its own timeout (an unreachable API at startup,
				// or a slow token endpoint) as a TaskCanceledException too; that is just a failed probe, handled below.
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

			using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
			var keepAlive = KeepSignedInAsync(apiClient, keepAliveCts.Token);
			try
			{
				await SmtpRelayRunner.RunAsync(Options.Smtp, telemetry, apiClient, token).ConfigureAwait(false);
			}
			finally
			{
				keepAliveCts.Cancel();
				try { await keepAlive.ConfigureAwait(false); }
				catch { /* the keep-alive never faults the relay */ }
			}
		}

		/// <summary>
		/// The relay only signs in when mail arrives, and Resgrid refresh tokens expire after about two days unused, so a quiet
		/// relay (a weekend without pages) lost its sign-in and the next real page failed. Touch the API every few hours so
		/// the token is refreshed (and rotated into the token cache) while the relay is idle; this also keeps the API pill
		/// current. The system API key never expires, so it needs none of this.
		/// </summary>
		private async Task KeepSignedInAsync(ResgridV4ApiClient apiClient, CancellationToken token)
		{
			if (Options.Resgrid?.GrantType == ResgridAuthGrantType.SystemApiKey)
				return;

			var interval = TimeSpan.FromHours(6);
			try
			{
				while (true)
				{
					await Task.Delay(interval, token).ConfigureAwait(false);
					try
					{
						var healthy = await apiClient.IsHealthyAsync(token).ConfigureAwait(false);
						MutableStatus.ResgridApi = healthy ? ConnectionState.Connected : ConnectionState.Degraded;
					}
					catch (OperationCanceledException) when (token.IsCancellationRequested)
					{
						return;
					}
					catch (Exception ex)
					{
						MutableStatus.ResgridApi = ConnectionState.Disconnected;
						Logger?.Warning(ex, "Keeping the Resgrid sign-in fresh failed; trying again in {Hours} hours", interval.TotalHours);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Stopping.
			}
		}
	}
}
