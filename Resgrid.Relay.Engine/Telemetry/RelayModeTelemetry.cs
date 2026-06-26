using Resgrid.Relay.Engine.Configuration;
using Serilog;

namespace Resgrid.Relay.Engine.Telemetry
{
	/// <summary>
	/// Factory that picks the right <see cref="IRelayModeTelemetry"/> for the current
	/// configuration: a Sentry-backed reporter when a DSN is present, otherwise the
	/// shared no-op singleton.
	/// </summary>
	public static class RelayModeTelemetry
	{
		public static IRelayModeTelemetry Create(RelayTelemetryOptions telemetry, ILogger logger) =>
			string.IsNullOrWhiteSpace(telemetry?.Sentry?.Dsn)
				? NullRelayModeTelemetry.Instance
				: new SentryRelayModeTelemetry(telemetry.Sentry, telemetry.Environment, logger);
	}
}
