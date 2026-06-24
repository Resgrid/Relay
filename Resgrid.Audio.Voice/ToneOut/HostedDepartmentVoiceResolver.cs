using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Connection;

namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// Hosted (Resgrid staff) multi-department tone-out resolver. SCAFFOLD — wired for
	/// the deferred hosted scenario where a single relay tones out calls into the PTT
	/// channels of ANY configured department.
	///
	/// Two completion paths are intended:
	///  1. Pass the target departmentId to <see cref="IVoiceChannelProvider"/> when the
	///     Resgrid Voice API supports system-key, on-behalf-of resolution (preferred —
	///     reuses the server-minted tokens), or
	///  2. Mint LiveKit tokens directly with Livekit.Server.Sdk.Dotnet using the
	///     department's voice channel room ids + the LiveKit API key/secret.
	///
	/// Until the hosted API surface is finalized this delegates to the provider with an
	/// explicit departmentId and throws a clear error if that path is unavailable.
	/// </summary>
	public sealed class HostedDepartmentVoiceResolver : IDepartmentVoiceResolver
	{
		private readonly IVoiceChannelProvider _channels;

		public HostedDepartmentVoiceResolver(IVoiceChannelProvider channels)
		{
			_channels = channels;
		}

		public async Task<IReadOnlyList<VoiceChannel>> ResolveAnnouncementChannelsAsync(
			string departmentId, string channelSelector, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(departmentId))
				throw new InvalidOperationException(
					"Hosted tone-out requires an explicit target departmentId. " +
					"Configure the SystemApiKey grant and the department to announce to.");

			// Path 1: ask the Voice API for the department's channels on behalf of the
			// system key. When the hosted API surface lands this returns server-minted
			// tokens just like the customer path.
			var channel = await _channels.GetChannelAsync(channelSelector, departmentId, cancellationToken).ConfigureAwait(false);
			return new[] { channel };
		}
	}
}
