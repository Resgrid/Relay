using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Where a recorded transmission's audio was persisted (local path or S3 URI).
	/// </summary>
	public sealed class StoredLocation
	{
		[JsonPropertyName("kind")] public string Kind { get; set; }
		[JsonPropertyName("location")] public string Location { get; set; }
		[JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
	}

	/// <summary>
	/// Compliance metadata for a single recorded transmission (one talk-spurt) on a
	/// PTT channel: who transmitted, when, for how long, on which channel, and where
	/// the audio is stored. One of these is appended to the transmission log per
	/// transmission for legal/after-action retention.
	/// </summary>
	public sealed class TransmissionRecord
	{
		[JsonPropertyName("id")] public string Id { get; set; }
		[JsonPropertyName("channelId")] public string ChannelId { get; set; }
		[JsonPropertyName("channelName")] public string ChannelName { get; set; }
		[JsonPropertyName("roomName")] public string RoomName { get; set; }

		[JsonPropertyName("participantIdentity")] public string ParticipantIdentity { get; set; }
		[JsonPropertyName("participantName")] public string ParticipantName { get; set; }
		[JsonPropertyName("trackSid")] public string TrackSid { get; set; }

		[JsonPropertyName("startUtc")] public DateTime StartUtc { get; set; }
		[JsonPropertyName("endUtc")] public DateTime EndUtc { get; set; }
		[JsonPropertyName("durationMs")] public long DurationMs { get; set; }

		[JsonPropertyName("sampleRate")] public int SampleRate { get; set; }
		[JsonPropertyName("channels")] public int Channels { get; set; }
		[JsonPropertyName("codec")] public string Codec { get; set; }
		[JsonPropertyName("samples")] public long Samples { get; set; }

		[JsonPropertyName("locations")] public List<StoredLocation> Locations { get; set; } = new List<StoredLocation>();
	}
}
