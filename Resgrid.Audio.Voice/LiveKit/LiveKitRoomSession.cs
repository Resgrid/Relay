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
			var loop = new TrackReadLoop(e.Track, sid, participant, OnFrame, OnReadLoopEnded, _logger);
			while (true)
			{
				if (_readLoops.TryAdd(sid, loop))
				{
					_logger?.Debug("Subscribed audio track {Sid} from {Participant}", sid, participant);
					loop.Start();
					return;
				}

				if (!_readLoops.TryGetValue(sid, out var existing))
					continue; // removed in the meantime: add ours

				if (!existing.IsCompleted)
				{
					// Already being read; this subscription is a duplicate.
					loop.Discard();
					return;
				}

				// The previous loop for this SID ended on its own but was still registered, which used to drop the re-subscribe
				// and leave that participant silent for the rest of the session. Replace it.
				if (_readLoops.TryUpdate(sid, loop, existing))
				{
					_logger?.Information("Re-subscribed audio track {Sid} from {Participant} (its previous reader had ended)", sid, participant);
					loop.Start();
					return;
				}
			}
		}

		private void OnReadLoopEnded(TrackReadLoop loop, bool stopped)
		{
			// Only remove the entry if it is still this loop, not a newer one registered under the same SID.
			if (_readLoops.TryRemove(new System.Collections.Generic.KeyValuePair<string, TrackReadLoop>(loop.Sid, loop)) && !stopped)
				_logger?.Warning("Audio from track {Sid} ({Participant}) stopped: its read loop ended on its own; it will resume if the track is re-subscribed",
					loop.Sid, loop.Participant);
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
			private readonly Action<TrackReadLoop, bool> _onEnded;
			private readonly ILogger _logger;
			private readonly CancellationTokenSource _cts = new CancellationTokenSource();
			private Task _task;
			private volatile bool _completed;

			public TrackReadLoop(Track track, string sid, VoiceParticipant participant, Action<VoiceAudioFrame> onFrame,
				Action<TrackReadLoop, bool> onEnded, ILogger logger)
			{
				_track = track;
				_sid = sid;
				_participant = participant;
				_onFrame = onFrame;
				_onEnded = onEnded;
				_logger = logger;
			}

			public string Sid => _sid;
			public VoiceParticipant Participant => _participant;

			/// <summary>True once the pump has exited (stopped, or ended on its own).</summary>
			public bool IsCompleted => _completed;

			public void Start() => _task = Task.Run(PumpWithNotifyAsync);

			/// <summary>Releases a loop that was never started.</summary>
			public void Discard()
			{
				try { _cts.Dispose(); } catch { /* ignore */ }
			}

			private async Task PumpWithNotifyAsync()
			{
				try
				{
					await PumpAsync().ConfigureAwait(false);
				}
				finally
				{
					_completed = true;
					try { _onEnded?.Invoke(this, _cts.IsCancellationRequested); }
					catch (Exception ex) { _logger?.Debug(ex, "Read loop end handler failed for track {Sid}", _sid); }
				}
			}

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
						try
						{
							_onFrame(new VoiceAudioFrame(_participant, _sid, pcm, DateTime.UtcNow));
						}
						catch (Exception ex)
						{
							// A subscriber that throws (a PTT serial write during a USB glitch, say) must cost one frame, not
							// end this participant's audio for the rest of the session with nothing but a Debug line.
							_logger?.Warning(ex, "Audio frame handler failed for track {Sid}", _sid);
						}
					}
				}
				catch (OperationCanceledException)
				{
					// normal shutdown
				}
				catch (Exception ex)
				{
					if (_cts.IsCancellationRequested)
						_logger?.Debug(ex, "Audio read loop for track {Sid} ended", _sid);
					else
						_logger?.Warning(ex, "Audio read loop for track {Sid} failed", _sid);
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
