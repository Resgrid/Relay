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
		private readonly string _fallbackDirectory;

		// Cancelled ShutdownGrace into DisposeAsync: uploads still running then are abandoned (their audio goes to the fallback
		// folder) so a store outage cannot hang shutdown.
		private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

		// Finalizations started by the scan timer or a rollover: DisposeAsync waits for them, because the callers dispose the S3
		// client and the metadata log right after it, and a transmission still uploading would otherwise be lost.
		private readonly ConcurrentDictionary<Task, byte> _pendingFinalizations = new ConcurrentDictionary<Task, byte>();

		private readonly ConcurrentDictionary<string, Segment> _segments =
			new ConcurrentDictionary<string, Segment>(StringComparer.Ordinal);
		private Timer _scanTimer;
		private int _started;

		// One admission gate per store, one for the fallback folder and one for the metadata log (see CallGate).
		private readonly Dictionary<ITransmissionStore, CallGate> _storeGates;
		private readonly CallGate _fallbackGate = new CallGate();
		private readonly CallGate _logGate = new CallGate();

		// Set when DisposeAsync begins; frames arriving after it are dropped. Together with the in-flight count it lets
		// DisposeAsync wait out a frame callback already running: a rollover takes its segment out of _segments before it
		// registers the finalization, and shutdown must not take its snapshot in between and miss that transmission.
		private int _disposing;
		private int _framesInFlight;

		/// <summary>Raised after a transmission is fully persisted.</summary>
		public event EventHandler<TransmissionRecord> TransmissionRecorded;

		/// <param name="fallbackDirectory">
		/// Where a transmission's WAV is written (date-partitioned) when no configured store could take it and no local copy
		/// exists — an S3 outage, say. Null disables the fallback (the audio of such a transmission is then lost).
		/// </param>
		public TransmissionRecorder(
			IVoiceRoomSession session,
			RecorderSettings settings,
			IReadOnlyList<ITransmissionStore> stores,
			ITransmissionLog log,
			ILogger logger,
			string fallbackDirectory = null)
		{
			_session = session ?? throw new ArgumentNullException(nameof(session));
			_settings = settings ?? new RecorderSettings();
			_stores = stores ?? Array.Empty<ITransmissionStore>();
			_storeGates = new Dictionary<ITransmissionStore, CallGate>(ReferenceEqualityComparer.Instance);
			foreach (var store in _stores)
			{
				if (store != null && !_storeGates.ContainsKey(store))
					_storeGates.Add(store, new CallGate());
			}
			_log = log;
			_logger = logger;
			_fallbackDirectory = string.IsNullOrWhiteSpace(fallbackDirectory) ? null : fallbackDirectory;
		}

		/// <summary>The most one store save or metadata-log append may take before it is abandoned.</summary>
		public TimeSpan StoreTimeout { get; set; } = TimeSpan.FromSeconds(60);

		/// <summary>How long uploads still running at <see cref="DisposeAsync"/> may continue before they are abandoned.</summary>
		public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(10);

		/// <summary>
		/// Records audio that does not arrive through the session, such as the radio side of a bridge (LiveKit does not deliver
		/// a participant's own published track back to it). Segmented like any remote track, keyed by the frame's TrackSid.
		/// </summary>
		public void Record(VoiceAudioFrame frame) => OnAudioFrame(this, frame);

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
			// Counted before the disposing check (both are full fences), so DisposeAsync either sees this callback in flight or
			// this callback sees DisposeAsync has begun.
			Interlocked.Increment(ref _framesInFlight);
			try
			{
				if (Volatile.Read(ref _disposing) == 0)
					AppendFrame(frame);
			}
			finally
			{
				Interlocked.Decrement(ref _framesInFlight);
			}
		}

		private void AppendFrame(VoiceAudioFrame frame)
		{
			if (frame?.Pcm == null || frame.Pcm.Length == 0 || string.IsNullOrEmpty(frame.TrackSid))
				return;

			bool active = AudioFormat.Dbfs(frame.Pcm) > _settings.SilenceFloorDbfs;
			var now = frame.TimestampUtc;

			// LiveKit delivers silence/comfort-noise frames for every track all the time. Only audible audio starts a
			// transmission; starting one on silence put up to the hang time of leading silence (and an early StartUtc) on
			// every recording.
			Segment seg;
			if (active)
				seg = _segments.GetOrAdd(frame.TrackSid, _ => new Segment(frame.TrackSid, frame.Participant, now));
			else if (!_segments.TryGetValue(frame.TrackSid, out seg))
				return;

			bool rollover;
			lock (seg.Sync)
			{
				seg.Append(frame.Pcm, active, now);
				rollover = seg.DurationSeconds >= _settings.MaxSegmentSeconds;
			}

			if (rollover && _segments.TryRemove(frame.TrackSid, out var rolled))
				StartFinalize(rolled);
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
					StartFinalize(ready);
			}
		}

		private void StartFinalize(Segment seg)
		{
			var task = FinalizeAsync(seg);
			_pendingFinalizations.TryAdd(task, 0);
			_ = task.ContinueWith(t => _pendingFinalizations.TryRemove(t, out _), TaskScheduler.Default);
		}

		private async Task FinalizeAsync(Segment seg)
		{
			if (!seg.TryClaimFinalize())
				return;

			short[] pcm;
			DateTime start, end;
			bool hadActive;
			long activeSamples;
			lock (seg.Sync)
			{
				pcm = seg.Pcm.ToArray();
				start = seg.StartUtc;
				end = seg.LastFrameUtc;
				hadActive = seg.HadActive;
				activeSamples = seg.ActiveSamples;
			}

			double durationMs = pcm.Length / (double)AudioFormat.SampleRate * 1000.0;

			// Judged on the audible part only: the buffered audio always carries the trailing hang, so measuring all of it let a
			// single click above the silence floor through as a ~2 s "transmission".
			double activeMs = activeSamples / (double)AudioFormat.SampleRate * 1000.0;
			if (!hadActive || activeMs < _settings.MinActiveMs)
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

				var failedStores = new List<string>();
				var haveLocalCopy = false;
				foreach (var store in _stores)
				{
					try
					{
						var location = await SaveBoundedAsync(store, _storeGates[store], objectName, wav, abandonOnShutdown: true).ConfigureAwait(false);
						record.Locations.Add(location);
						if (string.Equals(store.Kind, "local", StringComparison.OrdinalIgnoreCase))
							haveLocalCopy = true;
					}
					catch (Exception ex)
					{
						failedStores.Add(store.Kind);
						_logger?.Error(ex, "Failed to store transmission {Object} to {Store}", objectName, store.Kind);
					}
				}

				// A failed save used to leave only a metadata row pointing nowhere: the audio was gone. Without a local copy,
				// keep it in the fallback folder so the outage costs an upload, not the recording.
				if (failedStores.Count > 0 && !haveLocalCopy)
				{
					var fallback = await SaveFallbackAsync(objectName, wav, failedStores).ConfigureAwait(false);
					if (fallback != null)
						record.Locations.Add(fallback);
				}

				if (_log != null)
				{
					try
					{
						using var cts = new CancellationTokenSource(StoreTimeout);
						var token = cts.Token;
						await RunBounded(_logGate, "metadata log", () => _log.AppendAsync(record, token), token).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						_logger?.Error(ex, "Failed to write the metadata row for transmission {Id} on {Channel} (audio at {Locations})",
							record.Id, record.ChannelName, record.Locations.Count == 0 ? "nowhere" : string.Join(", ", record.Locations.Select(l => l.Location)));
					}
				}

				if (record.Locations.Count == 0)
				{
					_logger?.Error("Transmission {Id} ({Duration} ms from {Participant} on {Channel}) was NOT saved anywhere; its audio is lost",
						record.Id, record.DurationMs, record.ParticipantName ?? record.ParticipantIdentity ?? "unknown", record.ChannelName);
					return;
				}

				_logger?.Information("Recorded {Duration} ms from {Participant} on {Channel}",
					record.DurationMs, record.ParticipantName ?? record.ParticipantIdentity ?? "unknown", record.ChannelName);
				TransmissionRecorded?.Invoke(this, record);
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to finalize transmission on channel {Channel}", _session.ChannelName);
			}
		}

		// A store that never answers (an S3 endpoint black-holing requests) must not hold a finalization, and with it shutdown,
		// forever: the call gets StoreTimeout and, when it is an upload, no more than ShutdownGrace once disposal starts. WaitAsync
		// bounds it even if the store ignores the token.
		private async Task<StoredLocation> SaveBoundedAsync(ITransmissionStore store, CallGate gate, string objectName, byte[] wav, bool abandonOnShutdown)
		{
			using var cts = abandonOnShutdown
				? CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token)
				: new CancellationTokenSource();
			cts.CancelAfter(StoreTimeout);
			var token = cts.Token;   // read here: the call can still be starting on the pool after cts is disposed
			try
			{
				var location = await RunBounded(gate, $"{store.Kind} store", () => store.SaveAsync(objectName, wav, token), token).ConfigureAwait(false);
				if (location == null)
					throw new InvalidOperationException($"The {store.Kind} store returned no location.");
				return location;
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				throw new TimeoutException(abandonOnShutdown && _shutdownCts.IsCancellationRequested
					? $"The {store.Kind} store did not finish before shutdown."
					: $"The {store.Kind} store did not respond within {StoreTimeout.TotalSeconds:0} s.");
			}
		}

		// WaitAsync can only bound a task it has been handed, and an async store or log can block before it returns one
		// (LocalFileTransmissionStore creates its folder synchronously, which hangs on an unreachable network share). Starting
		// the call on the thread pool puts that part inside the timeout too, and off the finalization (or disposing) thread.
		// The timeout releases this transmission, not the call: one that outlives it is handed to the gate, which admits no
		// further call to that target until it returns.
		private static async Task<T> RunBounded<T>(CallGate gate, string target, Func<Task<T>> call, CancellationToken token)
		{
			gate.ThrowIfBlocked(target);
			var running = Task.Run(call, token);
			try
			{
				return await running.WaitAsync(token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!running.IsCompleted)
			{
				gate.Abandon(running);
				throw;
			}
		}

		private static Task RunBounded(CallGate gate, string target, Func<Task> call, CancellationToken token) =>
			RunBounded(gate, target, async () =>
			{
				await call().ConfigureAwait(false);
				return true;
			}, token);

		private async Task<StoredLocation> SaveFallbackAsync(string objectName, byte[] wav, IReadOnlyList<string> failedStores)
		{
			var failed = string.Join("+", failedStores);
			if (_fallbackDirectory == null)
			{
				_logger?.Error("Transmission {Object} could not be stored to {Stores} and no fallback folder is configured", objectName, failed);
				return null;
			}

			try
			{
				var saved = await SaveBoundedAsync(new LocalFileTransmissionStore(_fallbackDirectory), _fallbackGate, objectName, wav, abandonOnShutdown: false)
					.ConfigureAwait(false);
				_logger?.Warning("Transmission {Object} could not be stored to {Stores}; saved to the fallback folder instead at {Path} (upload it manually)",
					objectName, failed, saved.Location);
				return new StoredLocation { Kind = "fallback", Location = saved.Location, SizeBytes = saved.SizeBytes };
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Transmission {Object} could not be stored to {Stores} nor to the fallback folder {Folder}", objectName, failed, _fallbackDirectory);
				return null;
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
			// Capped: a remote participant's display name is unbounded, and a file name past the filesystem limit lost the
			// local copy of the recording.
			if (value.Length > 64)
				value = value.Substring(0, 64);

			var sb = new StringBuilder(value.Length);
			foreach (var c in value)
				sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
			return sb.ToString().Trim('_');
		}

		public async ValueTask DisposeAsync()
		{
			Interlocked.Exchange(ref _disposing, 1);
			_session.AudioFrameReceived -= OnAudioFrame;

			// Unsubscribing does not stop a delivery already under way (nor a Record call from another thread). Wait those out
			// so a rollover that already took its segment out of _segments has registered its finalization before the snapshot
			// below; later frames see _disposing and are dropped.
			while (Volatile.Read(ref _framesInFlight) > 0)
				await Task.Delay(1).ConfigureAwait(false);

			// Also waits for a scan callback that is running.
			if (_scanTimer != null)
				await _scanTimer.DisposeAsync().ConfigureAwait(false);

			// Flush any in-flight transmissions alongside the ones the timer already handed off (in parallel: one at a time,
			// each waiting out a dead store, multiplied the shutdown time by the number of open transmissions).
			foreach (var kvp in _segments.ToArray())
			{
				if (_segments.TryRemove(kvp.Key, out var seg))
					StartFinalize(seg);
			}

			// Uploads get ShutdownGrace to finish; whatever is still uploading then is saved to the fallback folder. Armed even
			// when nothing is pending, so no upload can outlast it.
			try { _shutdownCts.CancelAfter(ShutdownGrace); }
			catch (ObjectDisposedException) { }

			var pending = _pendingFinalizations.Keys.ToArray();
			if (pending.Length > 0)
			{
				try
				{
					await Task.WhenAll(pending).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_logger?.Warning(ex, "A recording finalization failed during shutdown");
				}
			}
		}

		/// <summary>
		/// Admission to one store (or the fallback folder, or the metadata log). A call blocked inside the store (a filesystem
		/// outage blocks LocalFileTransmissionStore's synchronous folder creation) keeps its pool worker and the transmission's
		/// WAV buffer after the timeout has released the recorder, and nothing can reclaim them. While such an abandoned call is
		/// still running, every new call to that target is refused at once, so the transmission goes straight to the fallback
		/// instead of tying up another worker and buffer the same way; the target is admitted again once the call returns.
		/// </summary>
		private sealed class CallGate
		{
			private int _abandoned;

			public void ThrowIfBlocked(string target)
			{
				if (Volatile.Read(ref _abandoned) > 0)
					throw new InvalidOperationException(
						$"The {target} is still blocked in an earlier call that outlived its timeout; nothing more is sent to it until that call returns.");
			}

			public void Abandon(Task running)
			{
				Interlocked.Increment(ref _abandoned);
				_ = running.ContinueWith(t =>
				{
					_ = t.Exception; // observed: nobody awaits an abandoned call
					Interlocked.Decrement(ref _abandoned);
				}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
			public long ActiveSamples { get; private set; }
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
					ActiveSamples += pcm.Length;
				}
			}

			public bool TryClaimFinalize() => Interlocked.Exchange(ref _finalized, 1) == 0;
		}
	}
}
