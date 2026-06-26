using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Sentry;
using Serilog;

namespace Resgrid.Relay.Engine.Telemetry
{
	/// <summary>
	/// Sends LiveKit voice-mode lifecycle/fault events to Sentry. Mirrors the init,
	/// flush, and per-call try/catch guarding of <see cref="Smtp.SmtpTelemetry"/>. When
	/// the DSN is blank the SDK is never initialized and every method is a no-op, so an
	/// empty configuration costs nothing.
	/// </summary>
	public sealed class SentryRelayModeTelemetry : IRelayModeTelemetry
	{
		private readonly ILogger _logger;
		private readonly IDisposable _sentrySdk;

		public SentryRelayModeTelemetry(SentryTelemetryOptions sentry, string environment, ILogger logger)
		{
			_logger = logger;

			if (!string.IsNullOrWhiteSpace(sentry?.Dsn))
			{
				_sentrySdk = SentrySdk.Init(sentryOptions =>
				{
					sentryOptions.Dsn = sentry.Dsn.Trim();
					sentryOptions.Environment = environment;
					sentryOptions.Release = sentry.Release;
					sentryOptions.ServerName = Environment.MachineName;
					sentryOptions.AttachStacktrace = true;
					sentryOptions.SendDefaultPii = sentry.SendDefaultPii;
					sentryOptions.ShutdownTimeout = TimeSpan.FromSeconds(2);
				});
			}

			_logger?.Information(
				"Relay mode observability initialized. SentryEnabled={SentryEnabled} Environment={Environment}",
				_sentrySdk != null,
				environment);
		}

		public void ModeStarting(string mode)
		{
			if (_sentrySdk == null)
				return;

			try
			{
				SentrySdk.AddBreadcrumb($"mode {mode} starting", category: "relay.mode");
			}
			catch (Exception sentryException)
			{
				_logger?.Warning(sentryException, "Failed sending relay mode breadcrumb to Sentry");
			}
		}

		public void ModeStopped(string mode)
		{
			if (_sentrySdk == null)
				return;

			try
			{
				SentrySdk.AddBreadcrumb($"mode {mode} stopped", category: "relay.mode");
			}
			catch (Exception sentryException)
			{
				_logger?.Warning(sentryException, "Failed sending relay mode breadcrumb to Sentry");
			}
		}

		public void ModeRetrying(string mode, Exception ex, int attempt, TimeSpan nextDelay)
		{
			if (_sentrySdk == null)
				return;

			try
			{
				SentrySdk.AddBreadcrumb(
					$"mode {mode} retrying",
					category: "relay.mode",
					level: BreadcrumbLevel.Warning,
					data: new Dictionary<string, string>
					{
						["attempt"] = attempt.ToString(),
						["next_delay_seconds"] = nextDelay.TotalSeconds.ToString("n2"),
						["exception"] = ex?.Message ?? string.Empty
					});
			}
			catch (Exception sentryException)
			{
				_logger?.Warning(sentryException, "Failed sending relay mode breadcrumb to Sentry");
			}
		}

		public void ModeFaulted(string mode, Exception ex)
		{
			if (_sentrySdk == null || ex == null)
				return;

			try
			{
				SentrySdk.CaptureException(ex, scope =>
				{
					scope.SetTag("relay.mode", mode);
					scope.SetTag("relay.scope", "mode_fault");
				});
			}
			catch (Exception sentryException)
			{
				_logger?.Warning(sentryException, "Failed sending relay mode exception to Sentry");
			}
		}

		public async ValueTask DisposeAsync()
		{
			if (_sentrySdk != null)
			{
				await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
				_sentrySdk.Dispose();
			}
		}
	}
}
