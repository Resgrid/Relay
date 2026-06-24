using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;

namespace Resgrid.Audio.Voice.Connection
{
	/// <summary>
	/// Resolves the PTT channels (LiveKit rooms + tokens) a relay should join.
	/// Backed by the Resgrid v4 Voice API. Abstracted so hosted (multi-department)
	/// resolution can be swapped in without touching the bridge/recorder/tone-out.
	/// </summary>
	public interface IVoiceChannelProvider
	{
		/// <summary>All voice channels for the (optional) department.</summary>
		Task<IReadOnlyList<VoiceChannel>> GetChannelsAsync(string departmentId = null, CancellationToken cancellationToken = default);

		/// <summary>
		/// Resolves a single channel by selector: a channel id, a channel name
		/// (case-insensitive), or null/empty/"default" for the department's default
		/// channel (falling back to the first channel).
		/// </summary>
		Task<VoiceChannel> GetChannelAsync(string selector, string departmentId = null, CancellationToken cancellationToken = default);

		/// <summary>
		/// Returns whether another participant may connect without exceeding the
		/// department's concurrent-seat subscription limit. Null = unknown (proceed).
		/// </summary>
		Task<bool?> CanConnectAsync(string departmentId = null, CancellationToken cancellationToken = default);
	}
}
