namespace Resgrid.Audio.Voice.Abstractions
{
	/// <summary>
	/// Factory for <see cref="IVoiceRoomSession"/>s. The concrete implementation
	/// (LiveKit) is the only place that references the WebRTC SDK, so the recorder,
	/// tone-out and radio bridge stay transport-agnostic and unit-testable.
	/// </summary>
	public interface IVoiceTransport
	{
		IVoiceRoomSession CreateSession(VoiceChannel channel);
	}
}
