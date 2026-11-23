using System;

namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// The relay is configured in a way that cannot work, so retrying would only repeat the failure. The resilience loop
	/// rethrows it immediately and the service faults with its message.
	/// </summary>
	public sealed class RelayConfigurationException : InvalidOperationException
	{
		public RelayConfigurationException(string message)
			: base(message)
		{
		}
	}
}
