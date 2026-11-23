using System;

namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// A settings file exists but could not be opened (another program holds it locked, or its permissions shut this account
	/// out). Unlike <see cref="RelayConfigurationException"/> the settings may well be fine and the next attempt may succeed, so
	/// it is not a configuration error: the console exits with an ordinary failure, which a service manager retries, instead of
	/// the configuration-error code that stops restarts.
	/// </summary>
	public sealed class RelaySettingsUnavailableException : Exception
	{
		public RelaySettingsUnavailableException(string message, Exception innerException)
			: base(message, innerException)
		{
		}
	}
}
