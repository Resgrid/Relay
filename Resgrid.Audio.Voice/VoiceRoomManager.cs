using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Serilog;

namespace Resgrid.Audio.Voice
{
	/// <summary>
	/// Owns and coordinates one or more <see cref="IVoiceRoomSession"/>s, enabling the
	/// multi-room scenarios (record several channels, bridge multiple radios). Each
	/// channel is joined at most once; callers attach their own sinks/sources to the
	/// returned session.
	/// </summary>
	public sealed class VoiceRoomManager : IAsyncDisposable
	{
		private readonly IVoiceTransport _transport;
		private readonly ILogger _logger;
		private readonly ConcurrentDictionary<string, IVoiceRoomSession> _sessions =
			new ConcurrentDictionary<string, IVoiceRoomSession>(StringComparer.Ordinal);

		public VoiceRoomManager(IVoiceTransport transport, ILogger logger)
		{
			_transport = transport ?? throw new ArgumentNullException(nameof(transport));
			_logger = logger;
		}

		public IReadOnlyCollection<IVoiceRoomSession> Sessions => _sessions.Values.ToList();

		public bool TryGet(string channelId, out IVoiceRoomSession session) =>
			_sessions.TryGetValue(channelId, out session);

		/// <summary>
		/// Joins a channel (idempotent per channel id) and returns the connected
		/// session. If the channel is already joined the existing session is returned.
		/// </summary>
		public async Task<IVoiceRoomSession> JoinAsync(VoiceChannel channel, CancellationToken cancellationToken = default)
		{
			if (channel == null)
				throw new ArgumentNullException(nameof(channel));

			if (_sessions.TryGetValue(channel.Id, out var existing))
				return existing;

			var session = _transport.CreateSession(channel);
			if (!_sessions.TryAdd(channel.Id, session))
			{
				await session.DisposeAsync().ConfigureAwait(false);
				return _sessions[channel.Id];
			}

			try
			{
				await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				_sessions.TryRemove(channel.Id, out _);
				await session.DisposeAsync().ConfigureAwait(false);
				throw;
			}

			return session;
		}

		public async Task LeaveAsync(string channelId)
		{
			if (_sessions.TryRemove(channelId, out var session))
				await session.DisposeAsync().ConfigureAwait(false);
		}

		public async ValueTask DisposeAsync()
		{
			foreach (var session in _sessions.Values)
			{
				try { await session.DisposeAsync().ConfigureAwait(false); }
				catch (Exception ex) { _logger?.Debug(ex, "Error disposing voice session {Channel}", session.ChannelId); }
			}
			_sessions.Clear();
		}
	}
}
