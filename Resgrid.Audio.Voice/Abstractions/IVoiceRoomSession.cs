using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Voice.Abstractions
{
	/// <summary>State of a session's connection to a PTT channel.</summary>
	public sealed class VoiceConnectionStateChange
	{
		public VoiceConnectionStateChange(bool connected, string reason)
		{
			Connected = connected;
			Reason = reason;
		}

		public bool Connected { get; }
		public string Reason { get; }
	}

	/// <summary>
	/// A live connection to a single Resgrid PTT channel (LiveKit room). Exposes
	/// inbound audio (per participant) and lets callers publish outbound audio. One
	/// session per channel; many sessions are coordinated by <c>VoiceRoomManager</c>.
	/// </summary>
	public interface IVoiceRoomSession : IAsyncDisposable
	{
		/// <summary>The channel/room id (DepartmentVoiceChannelId).</summary>
		string ChannelId { get; }

		/// <summary>Friendly channel name.</summary>
		string ChannelName { get; }

		bool IsConnected { get; }

		/// <summary>Connects and begins receiving subscribed audio.</summary>
		Task ConnectAsync(CancellationToken cancellationToken = default);

		/// <summary>Disconnects from the channel.</summary>
		Task DisconnectAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Publishes a local audio track and returns a publisher to feed it PCM.
		/// Call once per logical source (e.g. the radio, or tone-out). Most channels
		/// only need one local publisher.
		/// </summary>
		Task<IAudioPublisher> CreatePublisherAsync(string trackName, CancellationToken cancellationToken = default);

		/// <summary>Raised for every inbound PCM16 mono 48 kHz frame, tagged with sender.</summary>
		event EventHandler<VoiceAudioFrame> AudioFrameReceived;

		/// <summary>Raised when the channel connection state changes.</summary>
		event EventHandler<VoiceConnectionStateChange> ConnectionChanged;
	}
}
