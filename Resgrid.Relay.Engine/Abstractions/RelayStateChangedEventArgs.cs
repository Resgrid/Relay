using System;

namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// Raised by <see cref="IRelayService.StateChanged"/> when a service transitions
	/// between <see cref="RelayServiceState"/> values. <see cref="Error"/> carries the
	/// fault detail when <see cref="NewState"/> is <see cref="RelayServiceState.Faulted"/>.
	/// </summary>
	public sealed class RelayStateChangedEventArgs : EventArgs
	{
		public RelayStateChangedEventArgs(RelayServiceState oldState, RelayServiceState newState, string error = null)
		{
			OldState = oldState;
			NewState = newState;
			Error = error;
		}

		public RelayServiceState OldState { get; }

		public RelayServiceState NewState { get; }

		public string Error { get; }
	}
}
