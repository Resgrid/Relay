using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Rtc;
using Resgrid.Audio.Voice.Abstractions;
using Serilog;

namespace Resgrid.Audio.Voice.LiveKit
{
	/// <summary>
	/// A LiveKit-backed <see cref="IVoiceRoomSession"/>. Connects to one room,
	/// surfaces every inbound audio frame (tagged with the transmitting participant),
	/// and lets callers publish a local audio track. This is the single class that
	/// touches the LiveKit RTC SDK for receive/transmit.
	/// </summary>
	internal sealed class LiveKitRoomSession : IVoiceRoomSession
	{
		private readonly VoiceChannel _channel;
		private readonly int _publishQueueMs;
		private readonly ILogger _logger;

		private Room _room;
		private readonly ConcurrentDictionary<string, TrackReadLoop> _readLoops =
			new ConcurrentDictionary<string, TrackReadLoop>(StringComparer.Ordinal);
		private int _disposed;

		public LiveKitRoomSession(VoiceChannel channel, int publishQueueMs, ILogger logger)
		{
			_channel = channel;
			_publishQueueMs = publishQueueMs <= 0 ? 1000 : publishQueueMs;
			_logger = logger;
		}

		public string ChannelId => _channel.Id;
		public string ChannelName => _channel.Name;
		public bool IsConnected => _room != null && _room.IsConnected;

		public event EventHandler<VoiceAudioFrame> AudioFrameReceived;
		public event EventHandler<VoiceConnectionStateChange> ConnectionChanged;

		public async Task ConnectAsync(CancellationToken cancellationToken = default)
		{
			_room = new Room();

			_room.TrackSubscribed += OnTrackSubscribed;
			_room.TrackUnsubscribed += OnTrackUnsubscribed;
			// Initial connect is signaled explicitly after ConnectAsync below (deterministic,
			// fires exactly once); also subscribing to _room.Connected would double-notify.
			_room.Disconnected += (_, reason) => RaiseConnection(false, reason.ToString());
			_room.Reconnecting += (_, __) => RaiseConnection(false, "reconnecting");
			_room.Reconnected += (_, __) => RaiseConnection(true, "reconnected");

			var options = new RoomOptions { AutoSubscribe = true };

			_logger?.Information("Connecting to PTT channel {Channel} ({ChannelId}) at {Url}", _channel.Name, _channel.Id, _channel.RoomUrl);
			await _room.ConnectAsync(_channel.RoomUrl, _channel.Token, options, cancellationToken).ConfigureAwait(false);
			RaiseConnection(true, "connected");
		}

		public async Task<IAudioPublisher> CreatePublisherAsync(string trackName, CancellationToken cancellationToken = default)
		{
			if (_room == null)
				throw new InvalidOperationException("Connect the session before creating a publisher.");

			var source = new AudioSource(AudioFormat.SampleRate, AudioFormat.Channels, _publishQueueMs);
			var track = LocalAudioTrack.Create(trackName ?? "relay", source);
			var publishOptions = new TrackPublishOptions { Source = global::LiveKit.Proto.TrackSource.SourceMicrophone };

			await _room.LocalParticipant.PublishTrackAsync(track, publishOptions, cancellationToken).ConfigureAwait(false);
			_logger?.Information("Publishing local audio track '{Track}' on channel {Channel}", trackName, _channel.Name);

			return new LiveKitAudioPublisher(source, track, _room.LocalParticipant, _logger);
		}

		private void OnTrackSubscribed(object sender, TrackSubscribedEventArgs e)
		{
			if (e?.Track == null || e.Track.Kind != global::LiveKit.Proto.TrackKind.KindAudio)
				return;

			var participant = new VoiceParticipant(
				e.Participant?.Sid,
				e.Participant?.Identity,
				e.Participant?.Name,
				e.Participant?.Kind.ToString());

			var sid = e.Track.Sid ?? e.Publication?.Sid ?? Guid.NewGuid().ToString();
			var loop = new TrackReadLoop(e.Track, sid, participant, OnFrame, _logger);
			if (_readLoops.TryAdd(sid, loop))
			{
				_logger?.Debug("Subscribed audio track {Sid} from {Participant}", sid, participant);
				loop.Start();
			}
		}

		private void OnTrackUnsubscribed(object sender, TrackSubscribedEventArgs e)
		{
			var sid = e?.Track?.Sid ?? e?.Publication?.Sid;
			if (sid != null && _readLoops.TryRemove(sid, out var loop))
			{
				_logger?.Debug("Unsubscribed audio track {Sid}", sid);
				_ = loop.StopAsync();
			}
		}

		private void OnFrame(VoiceAudioFrame frame) => AudioFrameReceived?.Invoke(this, frame);

		private void RaiseConnection(bool connected, string reason) =>
			ConnectionChanged?.Invoke(this, new VoiceConnectionStateChange(connected, reason));

		public async Task DisconnectAsync(CancellationToken cancellationToken = default)
		{
			foreach (var kvp in _readLoops)
				await kvp.Value.StopAsync().ConfigureAwait(false);
			_readLoops.Clear();

			if (_room != null)
			{
				try { await _room.DisconnectAsync().ConfigureAwait(false); }
				catch (Exception ex) { _logger?.Debug(ex, "Room disconnect failed"); }
				RaiseConnection(false, "disconnected");
			}
		}

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 1)
				return;

			await DisconnectAsync().ConfigureAwait(false);
			try { _room?.Dispose(); } catch { /* best effort */ }
			_room = null;
		}

		/// <summary>
		/// Pumps a single subscribed audio track: reads 48 kHz mono frames from a
		/// LiveKit <see cref="AudioStream"/> and forwards each as a tagged
		/// <see cref="VoiceAudioFrame"/>.
		/// </summary>
		private sealed class TrackReadLoop
		{
			private readonly Track _track;
			private readonly string _sid;
			private readonly VoiceParticipant _participant;
			private readonly Action<VoiceAudioFrame> _onFrame;
			private readonly ILogger _logger;
			private readonly CancellationTokenSource _cts = new CancellationTokenSource();
			private Task _task;

			public TrackReadLoop(Track track, string sid, VoiceParticipant participant, Action<VoiceAudioFrame> onFrame, ILogger logger)
			{
				_track = track;
				_sid = sid;
				_participant = participant;
				_onFrame = onFrame;
				_logger = logger;
			}

			public void Start() => _task = Task.Run(PumpAsync);

			private async Task PumpAsync()
			{
				AudioStream stream = null;
				try
				{
					stream = AudioStream.FromTrack(
						_track,
						(uint)AudioFormat.SampleRate,
						(uint)AudioFormat.Channels,
						null,      // frameSizeMs — use the stream default
						200,       // capacity (frames buffered)
						null,      // no noise cancellation
						null);     // no frame processor

					while (!_cts.IsCancellationRequested)
					{
						var evt = await stream.ReadAsync(_cts.Token).ConfigureAwait(false);
						if (evt == null)
							break;

						var data = evt.Value.Frame?.DataArray;
						if (data == null || data.Length == 0)
							continue;

						var pcm = (short[])data.Clone();
						_onFrame(new VoiceAudioFrame(_participant, _sid, pcm, DateTime.UtcNow));
					}
				}
				catch (OperationCanceledException)
				{
					// normal shutdown
				}
				catch (Exception ex)
				{
					_logger?.Debug(ex, "Audio read loop for track {Sid} ended", _sid);
				}
				finally
				{
					if (stream != null)
					{
						try { await stream.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
					}
				}
			}

			public async Task StopAsync()
			{
				try { _cts.Cancel(); } catch { /* ignore */ }
				if (_task != null)
				{
					try { await _task.ConfigureAwait(false); } catch { /* ignore */ }
				}
				_cts.Dispose();
			}
		}
	}
}
