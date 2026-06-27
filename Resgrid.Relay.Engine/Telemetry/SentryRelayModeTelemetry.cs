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
		// SentrySdk.Init initializes a process-global hub and the returned IDisposable closes it,
		// so every LiveKit service shares ONE telemetry instance and ONE SDK handle. The shared
		// instance is reference-counted: the SDK is initialized on the first service and only
		// flushed/closed when the last service is disposed — otherwise the first service to stop
		// would tear Sentry down for the others still running.
		private static readonly object _sharedLock = new object();
		private static SentryRelayModeTelemetry _shared;
		private static int _refCount;

		private readonly ILogger _logger;
		private readonly IDisposable _sentrySdk;

		private SentryRelayModeTelemetry(SentryTelemetryOptions sentry, string environment, ILogger logger)
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

		/// <summary>
		/// Returns the process-wide shared telemetry, initializing the Sentry SDK on first use.
		/// Each call increments a reference count that <see cref="DisposeAsync"/> releases, so the
		/// SDK stays alive until every LiveKit service sharing it has been disposed.
		/// </summary>
		public static IRelayModeTelemetry Acquire(SentryTelemetryOptions sentry, string environment, ILogger logger)
		{
			lock (_sharedLock)
			{
				_shared ??= new SentryRelayModeTelemetry(sentry, environment, logger);
				_refCount++;
				return _shared;
			}
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
			IDisposable sdk;
			lock (_sharedLock)
			{
				// Release this service's reference; only the last one out flushes and closes the
				// shared SDK so any services still running keep reporting to Sentry.
				if (_refCount == 0 || --_refCount > 0)
					return;

				sdk = _sentrySdk;
				_shared = null;
			}

			if (sdk != null)
			{
				await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
				sdk.Dispose();
			}
		}
	}
}
