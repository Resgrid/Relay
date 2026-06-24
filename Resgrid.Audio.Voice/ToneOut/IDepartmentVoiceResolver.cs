using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Connection;

namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// Resolves which department(s)/channel(s) a dispatch tone-out targets. The
	/// customer resolver targets the relay's own department; the hosted resolver
	/// (Resgrid staff, multi-department) is scaffolded for a later phase.
	/// </summary>
	public interface IDepartmentVoiceResolver
	{
		Task<IReadOnlyList<VoiceChannel>> ResolveAnnouncementChannelsAsync(
			string departmentId, string channelSelector, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Customer (single-department) resolver: announces on the relay's own department
	/// voice channel(s) via the standard authenticated Voice API.
	/// </summary>
	public sealed class CustomerDepartmentVoiceResolver : IDepartmentVoiceResolver
	{
		private readonly IVoiceChannelProvider _channels;

		public CustomerDepartmentVoiceResolver(IVoiceChannelProvider channels)
		{
			_channels = channels;
		}

		public async Task<IReadOnlyList<VoiceChannel>> ResolveAnnouncementChannelsAsync(
			string departmentId, string channelSelector, CancellationToken cancellationToken = default)
		{
			var channel = await _channels.GetChannelAsync(channelSelector, departmentId, cancellationToken).ConfigureAwait(false);
			return new[] { channel };
		}
	}
}
