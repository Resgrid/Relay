using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Audio.Voice;
using Resgrid.Audio.Voice.Connection;
using Resgrid.Audio.Voice.LiveKit;
using Resgrid.Audio.Voice.ToneOut;
using Resgrid.Providers.ApiClient.V4;
using Serilog;

namespace Resgrid.Relay.Engine.Voice
{
	/// <summary>
	/// 'dispatch' mode: watches for new Resgrid calls and tones them out (alert tones +
	/// Resgrid TTS announcement) onto a department's PTT channel. Cross-platform.
	/// Customer single-department now; hosted multi-department is scaffolded.
	/// </summary>
	public static class DispatchVoiceMode
	{
		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, RelayStatus status = null)
		{
			if (string.IsNullOrWhiteSpace(options.Tts.ServiceBaseUrl))
			{
				logger.Error("Dispatch tone-out requires RESGRID__RELAY__Tts__ServiceBaseUrl (the Resgrid TTS service URL).");
				return 1;
			}

			using var apiClient = new ResgridV4ApiClient(options.Resgrid);
			var voiceApi = new VoiceApi(apiClient);
			var callsApi = new CallsApi(apiClient);

			var deptId = FirstNonEmpty(options.DispatchVoice.DepartmentId, options.Voice.DepartmentId);
			if (options.DispatchVoice.Hosted && string.IsNullOrWhiteSpace(deptId))
			{
				logger.Error("Hosted dispatch tone-out requires RESGRID__RELAY__DispatchVoice__DepartmentId.");
				return 1;
			}

			var transport = new LiveKitVoiceTransport(logger, options.Voice.PublishQueueMs);
			var provider = new ResgridVoiceChannelProvider(logger, voiceApi);
			await using var manager = new VoiceRoomManager(transport, logger);

			var channel = await provider.GetChannelAsync(options.DispatchVoice.Channel, deptId, cancellationToken).ConfigureAwait(false);
			var session = await manager.JoinAsync(channel, cancellationToken).ConfigureAwait(false);
			var publisher = await session.CreatePublisherAsync("dispatch", cancellationToken).ConfigureAwait(false);

			// Channel joined and session established — LiveKit is up.
			if (status != null)
				status.LiveKit = ConnectionState.Connected;

			using var tts = new ResgridTtsClient(options.Tts, logger);

			// TTS reachability is unverified until the first synthesis actually reaches the service,
			// so leave status.Tts as the caller set it (Connecting) and confirm Connected on the first
			// successful announcement below.

			var service = new DispatchToneOutService(tts, new ToneGenerator(), options.DispatchVoice.Tone, logger);

			// Prime "seen" with the current backlog so startup doesn't re-announce open calls.
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var call in await callsApi.GetActiveCallsAsync(deptId, cancellationToken).ConfigureAwait(false))
				if (!string.IsNullOrWhiteSpace(call.CallId))
					seen.Add(call.CallId);

			var pollSeconds = Math.Max(5, options.DispatchVoice.PollSeconds);
			logger.Information($"Dispatch tone-out on '{channel.Name}', polling new calls every {pollSeconds}s. Press Ctrl+C to stop.");

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					var calls = await callsApi.GetActiveCallsAsync(deptId, cancellationToken).ConfigureAwait(false);
					foreach (var call in calls)
					{
						if (string.IsNullOrWhiteSpace(call.CallId) || seen.Contains(call.CallId))
							continue;

						var text = VoiceModeRuntime.FormatCallAnnouncement(call);
						logger.Information("Toning out new call {CallId}", call.CallId);
						try
						{
							await service.AnnounceAsync(publisher, text, cancellationToken).ConfigureAwait(false);
							// A successful announcement means a real TTS synthesis call reached the service.
							if (status != null)
								status.Tts = ConnectionState.Connected;
							// Only mark the call handled once the announcement actually succeeds,
							// so a failed tone-out is retried on the next poll instead of being lost.
							seen.Add(call.CallId);
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception ex)
						{
							// Per-call failure: log and keep going so one bad announcement does not
							// block the rest of the batch; the call stays unseen and is retried next poll.
							logger.Error(ex, "Failed to tone out call {CallId}; will retry next poll", call.CallId);
						}
					}
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					logger.Error(ex, "Dispatch tone-out poll failed");
				}

				try { await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken).ConfigureAwait(false); }
				catch (TaskCanceledException) { break; }
			}

			await publisher.DisposeAsync().ConfigureAwait(false);
			return 0;
		}

		private static string FirstNonEmpty(params string[] values) =>
			values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
	}
}
