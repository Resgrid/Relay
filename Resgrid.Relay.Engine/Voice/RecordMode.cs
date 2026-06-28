using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Audio.Voice;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Connection;
using Resgrid.Audio.Voice.LiveKit;
using Resgrid.Audio.Voice.Recording;
using Resgrid.Providers.ApiClient.V4;
using Serilog;

namespace Resgrid.Relay.Engine.Voice
{
	/// <summary>
	/// 'record' mode: joins one or all PTT channels for a department and records every
	/// transmission (audio + metadata) for compliance. Cross-platform — runs on the
	/// desktop or in a Linux/Docker container.
	/// </summary>
	public static class RecordMode
	{
		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, RelayStatus status = null)
		{
			using var apiClient = new ResgridV4ApiClient(options.Resgrid);
			var voiceApi = new VoiceApi(apiClient);

			var deptId = FirstNonEmpty(options.Recorder.DepartmentId, options.Voice.DepartmentId);
			var transport = new LiveKitVoiceTransport(logger, options.Voice.PublishQueueMs);
			var provider = new ResgridVoiceChannelProvider(logger, voiceApi);
			await using var manager = new VoiceRoomManager(transport, logger);

			IReadOnlyList<VoiceChannel> channels;
			if (string.Equals(options.Recorder.Channel, "all", StringComparison.OrdinalIgnoreCase))
				channels = await provider.GetChannelsAsync(deptId, cancellationToken).ConfigureAwait(false);
			else
				channels = new[] { await provider.GetChannelAsync(options.Recorder.Channel, deptId, cancellationToken).ConfigureAwait(false) };

			var (stores, disposableStores) = BuildStores(options.Recorder, logger);
			var log = BuildLog(options.Recorder, logger);
			var recorders = new List<TransmissionRecorder>();

			// Signals a hard LiveKit disconnect (not a transient SDK reconnect) so the run
			// throws and the resilience layer rejoins. Carries the disconnect reason.
			var disconnect = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			var sessionHandlers = new List<(IVoiceRoomSession session, EventHandler<VoiceConnectionStateChange> handler)>();
			// The single LiveKit status pill is shared across every recorded channel, so track which
			// channels are mid-reconnect and surface the worst state: stay Degraded while ANY channel
			// is reconnecting and only return to Connected once they are all back. Connection events
			// fire on SDK threads, so guard the set and the status write with a lock.
			var degradedChannels = new HashSet<IVoiceRoomSession>();
			var statusLock = new object();

			try
			{
				foreach (var channel in channels)
				{
					var session = await manager.JoinAsync(channel, cancellationToken).ConfigureAwait(false);
					EventHandler<VoiceConnectionStateChange> handler = (_, change) =>
					{
						if (change.Connected)
						{
							// This channel recovered; only report the shared link healthy once EVERY
							// channel is back — a sibling still reconnecting keeps the pill Degraded.
							if (status != null)
								lock (statusLock)
								{
									degradedChannels.Remove(session);
									status.LiveKit = degradedChannels.Count == 0
										? ConnectionState.Connected
										: ConnectionState.Degraded;
								}
							return;
						}
						// "reconnecting" is the SDK auto-recovering — degrade but keep running.
						if (string.Equals(change.Reason, "reconnecting", StringComparison.OrdinalIgnoreCase))
						{
							if (status != null)
								lock (statusLock)
								{
									degradedChannels.Add(session);
									status.LiveKit = ConnectionState.Degraded;
								}
							return;
						}
						// Any other Connected=false is a hard disconnect — restart the recorder.
						disconnect.TrySetResult(change.Reason);
					};
					session.ConnectionChanged += handler;
					sessionHandlers.Add((session, handler));

					var recorder = new TransmissionRecorder(session, options.Recorder.Segmentation, stores, log, logger);
					if (status != null)
						recorder.TransmissionRecorded += (_, __) => status.IncrementTransmissionsRecorded();
					recorder.Start();
					recorders.Add(recorder);
				}

				// All requested channels joined — LiveKit is up, unless a channel already began
				// reconnecting during the join loop, in which case keep the shared pill Degraded.
				if (status != null)
					lock (statusLock)
						status.LiveKit = degradedChannels.Count == 0
							? ConnectionState.Connected
							: ConnectionState.Degraded;

				logger.Information($"Recording {channels.Count} channel(s) to {options.Recorder.Store}. Press Ctrl+C to stop.");
				var completed = await Task.WhenAny(VoiceModeRuntime.WaitForCancellationAsync(cancellationToken), disconnect.Task).ConfigureAwait(false);
				// Only fault on a real disconnect, not when a Ctrl+C/SIGTERM shutdown raced the
				// teardown's own disconnect event — a requested shutdown must complete cleanly.
				if (completed == disconnect.Task && !cancellationToken.IsCancellationRequested)
					throw new InvalidOperationException($"LiveKit session disconnected ({disconnect.Task.Result}); restarting recorder");
			}
			finally
			{
				// Detach connection handlers first so a teardown-time disconnect can't fire.
				foreach (var (session, handler) in sessionHandlers)
					session.ConnectionChanged -= handler;
				// Always tear down so a mid-loop JoinAsync failure or cancellation does not
				// leak already-created recorders, the metadata log, or disposable stores.
				foreach (var recorder in recorders)
					await recorder.DisposeAsync().ConfigureAwait(false);
				if (log != null)
					await log.DisposeAsync().ConfigureAwait(false);
				foreach (var disposable in disposableStores)
					disposable.Dispose();
			}

			return 0;
		}

		internal static (IReadOnlyList<ITransmissionStore> stores, List<IDisposable> disposables) BuildStores(RecorderModeOptions options, ILogger logger)
		{
			var stores = new List<ITransmissionStore>();
			var disposables = new List<IDisposable>();
			var kind = (options.Store ?? "local").ToLowerInvariant();

			if (kind == "local" || kind == "both")
				stores.Add(new LocalFileTransmissionStore(options.LocalPath));

			if (kind == "s3" || kind == "both")
			{
				var s3 = options.S3;
				if (string.IsNullOrWhiteSpace(s3.Bucket))
					throw new InvalidOperationException("Recorder S3 store selected but RESGRID__RELAY__Recorder__S3__Bucket is not set.");

				var store = S3TransmissionStore.Create(
					s3.Endpoint, s3.AccessKey, s3.SecretKey, s3.Region, s3.Bucket, s3.Prefix, s3.ForcePathStyle, s3.UseSsl);
				stores.Add(store);
				disposables.Add(store);
				logger.Information("Recorder S3 store: bucket={Bucket} prefix={Prefix}", s3.Bucket, s3.Prefix);
			}

			if (stores.Count == 0)
				stores.Add(new LocalFileTransmissionStore(options.LocalPath));

			return (stores, disposables);
		}

		internal static ITransmissionLog BuildLog(RecorderModeOptions options, ILogger logger)
		{
			switch ((options.Log ?? "jsonl").ToLowerInvariant())
			{
				case "none": return null;
				case "sqlite": return new SqliteTransmissionLog(options.LogPath);
				case "jsonl":
				default: return new JsonlTransmissionLog(options.LogPath);
			}
		}

		private static string FirstNonEmpty(params string[] values) =>
			values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
	}
}
