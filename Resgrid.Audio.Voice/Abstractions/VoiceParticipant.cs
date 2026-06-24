namespace Resgrid.Audio.Voice.Abstractions
{
	/// <summary>
	/// Immutable snapshot of a remote participant transmitting on a PTT channel.
	/// Captured when a track is subscribed so transmission logs can record exactly
	/// who transmitted (identity + display name).
	/// </summary>
	public sealed class VoiceParticipant
	{
		public VoiceParticipant(string sid, string identity, string name, string kind)
		{
			Sid = sid;
			Identity = identity;
			Name = name;
			Kind = kind;
		}

		/// <summary>LiveKit participant session id (unique per connection).</summary>
		public string Sid { get; }

		/// <summary>Stable participant identity (typically the Resgrid user id).</summary>
		public string Identity { get; }

		/// <summary>Human-friendly display name (caller id).</summary>
		public string Name { get; }

		/// <summary>Participant kind (Standard, Agent, Sip, Bridge, ...).</summary>
		public string Kind { get; }

		public override string ToString() =>
			string.IsNullOrWhiteSpace(Name) ? (Identity ?? Sid) : $"{Name} ({Identity})";
	}
}
