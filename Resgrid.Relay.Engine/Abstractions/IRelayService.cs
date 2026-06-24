using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// A single runnable relay mode (smtp, audio, radio, record, dispatch) with an
	/// explicit start/stop lifecycle suited to a button-driven desktop UI as well as
	/// the console. <see cref="StartAsync"/> is long-lived and returns when the service
	/// is cancelled or faults.
	/// </summary>
	public interface IRelayService : IAsyncDisposable
	{
		/// <summary>The mode key this service runs (e.g. "smtp", "radio").</summary>
		string Mode { get; }

		RelayServiceState State { get; }

		event EventHandler<RelayStateChangedEventArgs> StateChanged;

		IRelayStatus Status { get; }

		/// <summary>Runs the mode until <paramref name="token"/> is cancelled or a fault occurs.</summary>
		Task StartAsync(CancellationToken token);

		Task StopAsync();
	}
}
