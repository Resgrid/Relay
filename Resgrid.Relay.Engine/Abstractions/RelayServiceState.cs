namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// Lifecycle state of a single <see cref="IRelayService"/> instance.
	/// </summary>
	public enum RelayServiceState
	{
		Stopped,
		Starting,
		Running,
		Stopping,
		Faulted
	}
}
