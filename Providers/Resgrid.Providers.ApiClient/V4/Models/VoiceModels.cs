using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Resgrid.Providers.ApiClient.V4.Models
{
	// These models mirror the Resgrid Core v4 VoiceController responses
	// (DepartmentVoiceResult / CanConnectToVoiceSessionResult). They give the
	// Relay everything it needs to join a department's PTT channel (LiveKit room):
	// the websocket server URL, the per-channel room id (= DepartmentVoiceChannelId)
	// and a pre-minted LiveKit access token (JWT, ~24h TTL).

	/// <summary>
	/// Response from GET /api/v4/Voice/GetDepartmentVoiceSettings.
	/// </summary>
	public sealed class DepartmentVoiceResult
	{
		[JsonPropertyName("Data")]
		public DepartmentVoiceResultData Data { get; set; }

		[JsonPropertyName("Status")]
		public string Status { get; set; }
	}

	public sealed class DepartmentVoiceResultData
	{
		/// <summary>Whether voice (PTT) is enabled for the department.</summary>
		[JsonPropertyName("VoiceEnabled")]
		public bool VoiceEnabled { get; set; }

		/// <summary>VoipProviderType. 2 == LiveKit.</summary>
		[JsonPropertyName("Type")]
		public int Type { get; set; }

		/// <summary>VoIP domain / realm.</summary>
		[JsonPropertyName("Realm")]
		public string Realm { get; set; }

		/// <summary>
		/// Client-facing LiveKit websocket URL (e.g. wss://livekit.example.com).
		/// Pass this verbatim to Room.ConnectAsync.
		/// </summary>
		[JsonPropertyName("VoipServerWebsocketSslAddress")]
		public string VoipServerWebsocketSslAddress { get; set; }

		/// <summary>Display name embedded in the issued tokens (the current user).</summary>
		[JsonPropertyName("CallerIdName")]
		public string CallerIdName { get; set; }

		/// <summary>
		/// Encrypted department id used as the token for the anonymous
		/// CanConnectToVoiceSession seat-limit check.
		/// </summary>
		[JsonPropertyName("CanConnectApiToken")]
		public string CanConnectApiToken { get; set; }

		[JsonPropertyName("Channels")]
		public List<DepartmentVoiceChannelResultData> Channels { get; set; } = new List<DepartmentVoiceChannelResultData>();

		[JsonPropertyName("UserInfo")]
		public DepartmentVoiceUserInfoResultData UserInfo { get; set; }
	}

	public sealed class DepartmentVoiceChannelResultData
	{
		/// <summary>
		/// DepartmentVoiceChannelId. This IS the LiveKit room name to join.
		/// </summary>
		[JsonPropertyName("Id")]
		public string Id { get; set; }

		[JsonPropertyName("Name")]
		public string Name { get; set; }

		[JsonPropertyName("ConferenceNumber")]
		public int ConferenceNumber { get; set; }

		[JsonPropertyName("IsDefault")]
		public bool IsDefault { get; set; }

		/// <summary>Pre-minted LiveKit access token (JWT) granting roomJoin for this room.</summary>
		[JsonPropertyName("Token")]
		public string Token { get; set; }
	}

	public sealed class DepartmentVoiceUserInfoResultData
	{
		[JsonPropertyName("Username")]
		public string Username { get; set; }

		[JsonPropertyName("Password")]
		public string Password { get; set; }

		[JsonPropertyName("Pin")]
		public string Pin { get; set; }
	}

	/// <summary>
	/// Response from GET /api/v4/Voice/CanConnectToVoiceSession.
	/// Lets the relay honor a department's concurrent-seat subscription limit
	/// before joining/publishing.
	/// </summary>
	public sealed class CanConnectToVoiceSessionResult
	{
		[JsonPropertyName("Data")]
		public CanConnectToVoiceSessionResultData Data { get; set; }

		[JsonPropertyName("Status")]
		public string Status { get; set; }
	}

	public sealed class CanConnectToVoiceSessionResultData
	{
		[JsonPropertyName("CanConnect")]
		public bool CanConnect { get; set; }

		[JsonPropertyName("CurrentSessions")]
		public int CurrentSessions { get; set; }

		[JsonPropertyName("MaxSessions")]
		public int MaxSessions { get; set; }
	}
}
