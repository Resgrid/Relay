namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// Health of an individual external dependency a relay mode talks to
	/// (Resgrid API, LiveKit, Redis, SMTP listener, TTS). <see cref="NotApplicable"/>
	/// is used when a mode does not use that dependency at all, so the UI can grey it out.
	/// </summary>
	public enum ConnectionState
	{
		NotApplicable,
		Unknown,
		Connecting,
		Connected,
		Degraded,
		Disconnected
	}
}
