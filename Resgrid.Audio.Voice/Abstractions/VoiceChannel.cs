namespace Resgrid.Audio.Voice.Abstractions
{
	/// <summary>
	/// Everything needed to join one Resgrid PTT channel: the LiveKit room id, the
	/// websocket server URL and a pre-minted access token. Produced by an
	/// <c>IVoiceChannelProvider</c> from the Resgrid v4 Voice API.
	/// </summary>
	public sealed class VoiceChannel
	{
		public VoiceChannel(string id, string name, bool isDefault, string roomUrl, string token)
		{
			Id = id;
			Name = name;
			IsDefault = isDefault;
			RoomUrl = roomUrl;
			Token = token;
		}

		/// <summary>DepartmentVoiceChannelId — the LiveKit room name.</summary>
		public string Id { get; }

		public string Name { get; }

		public bool IsDefault { get; }

		/// <summary>Client websocket URL (wss://...) passed to Room.ConnectAsync.</summary>
		public string RoomUrl { get; }

		/// <summary>LiveKit JWT access token granting roomJoin for this room.</summary>
		public string Token { get; }
	}
}
