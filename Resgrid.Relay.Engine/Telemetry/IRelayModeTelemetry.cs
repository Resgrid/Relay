using System;
using System.Threading.Tasks;

namespace Resgrid.Relay.Engine.Telemetry
{
	/// <summary>
	/// Lightweight observability hooks for the long-lived LiveKit voice modes
	/// (radio / record / dispatch). Implementations forward lifecycle and
	/// fault/retry events to a backend (e.g. Sentry) — or do nothing when
	/// monitoring is disabled.
	/// </summary>
	public interface IRelayModeTelemetry : IAsyncDisposable
	{
		void ModeStarting(string mode);
		void ModeRetrying(string mode, Exception ex, int attempt, TimeSpan nextDelay);
		void ModeFaulted(string mode, Exception ex);
		void ModeStopped(string mode);
	}
}
