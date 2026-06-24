using System;

namespace Resgrid.Audio.Voice.Abstractions
{
	/// <summary>
	/// A decoded PCM16 mono 48 kHz audio frame received from a remote PTT participant,
	/// tagged with who sent it and on which track. Raised by an
	/// <see cref="IVoiceRoomSession"/> for every inbound frame so consumers (radio
	/// playout, recorder) can route/segment it.
	/// </summary>
	public sealed class VoiceAudioFrame
	{
		public VoiceAudioFrame(VoiceParticipant participant, string trackSid, short[] pcm, DateTime timestampUtc)
		{
			Participant = participant;
			TrackSid = trackSid;
			Pcm = pcm ?? Array.Empty<short>();
			TimestampUtc = timestampUtc;
		}

		/// <summary>The participant who transmitted this audio.</summary>
		public VoiceParticipant Participant { get; }

		/// <summary>The remote track id this frame came from.</summary>
		public string TrackSid { get; }

		/// <summary>PCM16 mono samples at 48 kHz.</summary>
		public short[] Pcm { get; }

		/// <summary>UTC time the frame was received.</summary>
		public DateTime TimestampUtc { get; }
	}
}
