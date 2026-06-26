using System;
using System.Threading.Tasks;

namespace Resgrid.Relay.Engine.Telemetry
{
	/// <summary>
	/// No-op <see cref="IRelayModeTelemetry"/> used when mode monitoring is disabled
	/// (non-LiveKit modes, or LiveKit modes with no Sentry DSN configured). Every call
	/// is a cheap nothing so callers never need to null-check the telemetry instance.
	/// </summary>
	public sealed class NullRelayModeTelemetry : IRelayModeTelemetry
	{
		public static readonly IRelayModeTelemetry Instance = new NullRelayModeTelemetry();

		private NullRelayModeTelemetry()
		{
		}

		public void ModeStarting(string mode)
		{
		}

		public void ModeRetrying(string mode, Exception ex, int attempt, TimeSpan nextDelay)
		{
		}

		public void ModeFaulted(string mode, Exception ex)
		{
		}

		public void ModeStopped(string mode)
		{
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
