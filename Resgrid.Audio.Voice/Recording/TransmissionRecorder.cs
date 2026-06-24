using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Audio;
using Serilog;

namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Records every transmission on a PTT channel for compliance. Subscribes to a
	/// session's inbound audio, segments it per participant talk-spurt (start on
	/// first audio, end after a silence hang), then writes a WAV to each configured
	/// store and appends a metadata row (who/when/duration/channel/where) to the log.
	/// </summary>
	public sealed class TransmissionRecorder : IAsyncDisposable
	{
		private readonly IVoiceRoomSession _session;
		private readonly RecorderSettings _settings;
		private readonly IReadOnlyList<ITransmissionStore> _stores;
		private readonly ITransmissionLog _log;
		private readonly ILogger _logger;

		private readonly ConcurrentDictionary<string, Segment> _segments =
			new ConcurrentDictionary<string, Segment>(StringComparer.Ordinal);
		private Timer _scanTimer;
		private int _started;

		/// <summary>Raised after a transmission is fully persisted.</summary>
		public event EventHandler<TransmissionRecord> TransmissionRecorded;

		public TransmissionRecorder(
			IVoiceRoomSession session,
			RecorderSettings settings,
			IReadOnlyList<ITransmissionStore> stores,
			ITransmissionLog log,
			ILogger logger)
		{
			_session = session ?? throw new ArgumentNullException(nameof(session));
			_settings = settings ?? new RecorderSettings();
			_stores = stores ?? Array.Empty<ITransmissionStore>();
			_log = log;
			_logger = logger;
		}

		public void Start()
		{
			if (Interlocked.Exchange(ref _started, 1) == 1)
				return;

			_session.AudioFrameReceived += OnAudioFrame;
			_scanTimer = new Timer(_ => ScanForEnded(), null, _settings.ScanIntervalMs, _settings.ScanIntervalMs);
			_logger?.Information("Recording channel {Channel} ({ChannelId}) to {Stores}",
				_session.ChannelName, _session.ChannelId, string.Join("+", _stores.Select(s => s.Kind)));
		}

		private void OnAudioFrame(object sender, VoiceAudioFrame frame)
		{
			if (frame?.Pcm == null || frame.Pcm.Length == 0)
				return;

			bool active = AudioFormat.Dbfs(frame.Pcm) > _settings.SilenceFloorDbfs;
			var now = frame.TimestampUtc;

			var seg = _segments.GetOrAdd(frame.TrackSid, _ => new Segment(frame.TrackSid, frame.Participant, now));
			bool rollover;
			lock (seg.Sync)
			{
				seg.Append(frame.Pcm, active, now);
				rollover = seg.DurationSeconds >= _settings.MaxSegmentSeconds;
			}

			if (rollover && _segments.TryRemove(frame.TrackSid, out var rolled))
				_ = FinalizeAsync(rolled);
		}

		private void ScanForEnded()
		{
			var cutoff = DateTime.UtcNow;
			foreach (var kvp in _segments.ToArray())
			{
				var seg = kvp.Value;
				bool ended;
				lock (seg.Sync)
					ended = (cutoff - seg.LastActiveUtc).TotalMilliseconds > _settings.HangMs;

				if (ended && _segments.TryRemove(kvp.Key, out var ready))
					_ = FinalizeAsync(ready);
			}
		}

		private async Task FinalizeAsync(Segment seg)
		{
			if (!seg.TryClaimFinalize())
				return;

			short[] pcm;
			DateTime start, end;
			bool hadActive;
			lock (seg.Sync)
			{
				pcm = seg.Pcm.ToArray();
				start = seg.StartUtc;
				end = seg.LastFrameUtc;
				hadActive = seg.HadActive;
			}

			double durationMs = pcm.Length / (double)AudioFormat.SampleRate * 1000.0;
			if (!hadActive || durationMs < _settings.MinActiveMs)
				return; // silence/comfort-noise only — not a real transmission

			try
			{
				var record = new TransmissionRecord
				{
					Id = Guid.NewGuid().ToString("N"),
					ChannelId = _session.ChannelId,
					ChannelName = _session.ChannelName,
					RoomName = _session.ChannelId,
					ParticipantIdentity = seg.Participant?.Identity,
					ParticipantName = seg.Participant?.Name,
					TrackSid = seg.TrackSid,
					StartUtc = start,
					EndUtc = end,
					DurationMs = (long)durationMs,
					SampleRate = AudioFormat.SampleRate,
					Channels = AudioFormat.Channels,
					Codec = "pcm_s16le",
					Samples = pcm.Length
				};

				var wav = WavIo.WritePcm16(pcm, AudioFormat.SampleRate, AudioFormat.Channels);
				var objectName = BuildObjectName(record);

				foreach (var store in _stores)
				{
					try
					{
						var location = await store.SaveAsync(objectName, wav).ConfigureAwait(false);
						record.Locations.Add(location);
					}
					catch (Exception ex)
					{
						_logger?.Error(ex, "Failed to store transmission to {Store}", store.Kind);
					}
				}

				if (_log != null)
					await _log.AppendAsync(record).ConfigureAwait(false);

				_logger?.Information("Recorded {Duration} ms from {Participant} on {Channel}",
					record.DurationMs, record.ParticipantName ?? record.ParticipantIdentity ?? "unknown", record.ChannelName);
				TransmissionRecorded?.Invoke(this, record);
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to finalize transmission on channel {Channel}", _session.ChannelName);
			}
		}

		private static string BuildObjectName(TransmissionRecord r)
		{
			var who = Sanitize(r.ParticipantName ?? r.ParticipantIdentity ?? "unknown");
			var chan = Sanitize(r.ChannelName ?? r.ChannelId ?? "channel");
			var ts = r.StartUtc.ToString("yyyyMMdd'T'HHmmss'Z'");
			return $"{chan}_{ts}_{who}_{r.Id.Substring(0, 8)}.wav";
		}

		private static string Sanitize(string value)
		{
			var sb = new StringBuilder(value.Length);
			foreach (var c in value)
				sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
			return sb.ToString().Trim('_');
		}

		public async ValueTask DisposeAsync()
		{
			_session.AudioFrameReceived -= OnAudioFrame;
			if (_scanTimer != null)
				await _scanTimer.DisposeAsync().ConfigureAwait(false);

			// Flush any in-flight transmissions.
			foreach (var kvp in _segments.ToArray())
			{
				if (_segments.TryRemove(kvp.Key, out var seg))
					await FinalizeAsync(seg).ConfigureAwait(false);
			}
		}

		/// <summary>An in-progress recording for one participant track.</summary>
		private sealed class Segment
		{
			public readonly object Sync = new object();
			private int _finalized;

			public Segment(string trackSid, VoiceParticipant participant, DateTime startUtc)
			{
				TrackSid = trackSid;
				Participant = participant;
				StartUtc = startUtc;
				LastFrameUtc = startUtc;
				LastActiveUtc = startUtc;
			}

			public string TrackSid { get; }
			public VoiceParticipant Participant { get; }
			public DateTime StartUtc { get; }
			public DateTime LastFrameUtc { get; private set; }
			public DateTime LastActiveUtc { get; private set; }
			public bool HadActive { get; private set; }
			public List<short> Pcm { get; } = new List<short>();
			public double DurationSeconds => Pcm.Count / (double)AudioFormat.SampleRate;

			public void Append(short[] pcm, bool active, DateTime now)
			{
				Pcm.AddRange(pcm);
				LastFrameUtc = now;
				if (active)
				{
					LastActiveUtc = now;
					HadActive = true;
				}
			}

			public bool TryClaimFinalize() => Interlocked.Exchange(ref _finalized, 1) == 0;
		}
	}
}
