using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using Serilog;

namespace Resgrid.Audio.Voice.Connection
{
	/// <summary>
	/// Resolves PTT channels from the Resgrid v4 Voice API
	/// (GET /api/v4/Voice/GetDepartmentVoiceSettings). Each channel carries the
	/// LiveKit server URL and a pre-minted join token. Assumes
	/// <see cref="ResgridV4ApiClient"/> has already been initialized.
	/// </summary>
	public sealed class ResgridVoiceChannelProvider : IVoiceChannelProvider
	{
		private readonly ILogger _logger;
		private readonly IResgridVoiceApi _voiceApi;

		public ResgridVoiceChannelProvider(ILogger logger, IResgridVoiceApi voiceApi)
		{
			_logger = logger;
			_voiceApi = voiceApi ?? throw new ArgumentNullException(nameof(voiceApi));
		}

		public async Task<IReadOnlyList<VoiceChannel>> GetChannelsAsync(string departmentId = null, CancellationToken cancellationToken = default)
		{
			var settings = await _voiceApi.GetDepartmentVoiceSettingsAsync(departmentId, cancellationToken).ConfigureAwait(false);
			if (settings == null)
				throw new InvalidOperationException("The Resgrid API returned no voice settings. Is voice/PTT enabled for this department?");

			if (!settings.VoiceEnabled)
				throw new InvalidOperationException("Voice/PTT is not enabled for this department.");

			if (string.IsNullOrWhiteSpace(settings.VoipServerWebsocketSslAddress))
				throw new InvalidOperationException("The Resgrid API did not return a LiveKit server URL (VoipServerWebsocketSslAddress).");

			var url = settings.VoipServerWebsocketSslAddress;
			var channels = (settings.Channels ?? new List<DepartmentVoiceChannelResultData>())
				.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Token))
				.Select(c => new VoiceChannel(c.Id, c.Name, c.IsDefault, url, c.Token))
				.ToList();

			if (channels.Count == 0)
				throw new InvalidOperationException("The department has no voice channels with valid tokens.");

			return channels;
		}

		public async Task<VoiceChannel> GetChannelAsync(string selector, string departmentId = null, CancellationToken cancellationToken = default)
		{
			var channels = await GetChannelsAsync(departmentId, cancellationToken).ConfigureAwait(false);

			if (string.IsNullOrWhiteSpace(selector) || selector.Equals("default", StringComparison.OrdinalIgnoreCase))
				return channels.FirstOrDefault(c => c.IsDefault) ?? channels[0];

			var byId = channels.FirstOrDefault(c => string.Equals(c.Id, selector, StringComparison.OrdinalIgnoreCase));
			if (byId != null)
				return byId;

			var byName = channels.FirstOrDefault(c => string.Equals(c.Name, selector, StringComparison.OrdinalIgnoreCase));
			if (byName != null)
				return byName;

			throw new InvalidOperationException(
				$"No voice channel matched '{selector}'. Available: {string.Join(", ", channels.Select(c => $"{c.Name} [{c.Id}]"))}.");
		}

		public async Task<bool?> CanConnectAsync(string departmentId = null, CancellationToken cancellationToken = default)
		{
			var settings = await _voiceApi.GetDepartmentVoiceSettingsAsync(departmentId, cancellationToken).ConfigureAwait(false);
			if (settings == null || string.IsNullOrWhiteSpace(settings.CanConnectApiToken))
				return null;

			var result = await _voiceApi.CanConnectToVoiceSessionAsync(settings.CanConnectApiToken, cancellationToken).ConfigureAwait(false);
			if (result == null)
				return null;

			if (!result.CanConnect)
				_logger?.Warning("Voice seat limit reached: {Current}/{Max} sessions in use.", result.CurrentSessions, result.MaxSessions);

			return result.CanConnect;
		}
	}
}
