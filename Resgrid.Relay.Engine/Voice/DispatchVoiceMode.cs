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
using Resgrid.Audio.Voice.ToneOut;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
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
		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, RelayStatus status = null,
			DispatchToneOutState state = null)
		{
			state ??= new DispatchToneOutState();

			// Configuration errors: retrying cannot fix them, so fault now with the reason instead of returning a failure code
			// that the resilience layer retries as if it were transient.
			if (string.IsNullOrWhiteSpace(options.Tts.ServiceBaseUrl))
				throw new RelayConfigurationException("Dispatch tone-out requires RESGRID__RELAY__Tts__ServiceBaseUrl (the Resgrid TTS service URL).");

			var deptId = FirstNonEmpty(options.DispatchVoice.DepartmentId, options.Voice.DepartmentId);
			if (options.DispatchVoice.Hosted && string.IsNullOrWhiteSpace(deptId))
				throw new RelayConfigurationException("Hosted dispatch tone-out requires RESGRID__RELAY__DispatchVoice__DepartmentId.");

			VoiceModeRuntime.EnsureUserTokenAuth(options, "dispatch");
			using var apiClient = new ResgridV4ApiClient(options.Resgrid);
			var voiceApi = new VoiceApi(apiClient);
			var callsApi = new CallsApi(apiClient);

			var transport = new LiveKitVoiceTransport(logger, options.Voice.PublishQueueMs);
			var provider = new ResgridVoiceChannelProvider(logger, voiceApi);

			if (!await VoiceModeRuntime.HasVoiceSeatAsync(options, provider, deptId, cancellationToken).ConfigureAwait(false))
			{
				logger.Error("Voice seat limit reached for this department; not connecting.");
				return 1;
			}

			await using var manager = new VoiceRoomManager(transport, logger);

			var channel = await provider.GetChannelAsync(options.DispatchVoice.Channel, deptId, cancellationToken).ConfigureAwait(false);
			var session = await manager.JoinAsync(channel, cancellationToken).ConfigureAwait(false);

			// Follow the connection from the moment the join returns (a drop while the publisher was being created used to go
			// unnoticed): a hard disconnect ends the run so the resilience layer rejoins, a transient SDK "reconnecting" only
			// degrades status, and either one holds tone-outs back until the channel can carry them again.
			using var monitor = new VoiceSessionMonitor(session, status);
			await monitor.EnsureConnectedAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

			IAudioPublisher publisher = null;
			// Enter the try BEFORE the remaining setup (publisher, TTS client, backlog prime) so the finally below always
			// disposes the publisher — a transient API failure during startup must not leak it.
			try
			{
			publisher = await session.CreatePublisherAsync("dispatch", cancellationToken).ConfigureAwait(false);

			// Channel joined and session established — LiveKit is up (unless it is already reconnecting).
			if (status != null && monitor.IsUsable)
				status.LiveKit = ConnectionState.Connected;

			using var tts = new ResgridTtsClient(options.Tts, logger);

			// TTS reachability is unverified until the first synthesis actually reaches the service:
			// report it as unprobed (Unknown) rather than a transitional Connecting that would stick,
			// and confirm Connected on the first successful announcement below.
			if (status != null)
				status.Tts = ConnectionState.Unknown;

			var service = new DispatchToneOutService(tts, new ToneGenerator(), options.DispatchVoice.Tone, logger);
			var priorityNames = await LoadPriorityNamesAsync(new CallPrioritiesApi(apiClient), logger, cancellationToken).ConfigureAwait(false);
			var giveUpAfter = TimeSpan.FromMinutes(Math.Max(1, options.DispatchVoice.GiveUpAfterMinutes));

			// Prime with the backlog once, when the relay first starts, so it doesn't re-announce calls that were already open.
			// A reconnect after a LiveKit/API failure must NOT re-prime: the calls created while the relay was down are exactly
			// the ones that still need toning out.
			var seen = state.Announced;
			if (!state.Primed)
				PrimeBacklog(await callsApi.GetActiveCallsAsync(deptId, cancellationToken).ConfigureAwait(false), state, DateTime.UtcNow);

			var pollSeconds = Math.Max(5, options.DispatchVoice.PollSeconds);
			logger.Information($"Dispatch tone-out on '{channel.Name}', polling new calls every {pollSeconds}s. Press Ctrl+C to stop.");

			while (!cancellationToken.IsCancellationRequested)
			{
				// A hard LiveKit disconnect ends the poll loop so resilience reconnects.
				if (monitor.IsDisconnected)
					throw new InvalidOperationException($"LiveKit dispatch session disconnected ({monitor.Disconnected.Result}); reconnecting");

				try
				{
					var calls = await callsApi.GetActiveCallsAsync(deptId, cancellationToken).ConfigureAwait(false);
					foreach (var call in calls)
					{
						if (call == null || string.IsNullOrWhiteSpace(call.CallId) || seen.Contains(call.CallId))
							continue;

						// A scheduled call is already "active" before its dispatch time; tone it out when it is actually sent.
						if (IsScheduledForLater(call, DateTime.UtcNow))
							continue;

						// A call whose tone-out keeps failing is retried every poll, but only for so long: past that the announcement
						// is stale, and a call the TTS service will never accept would otherwise be retried, and logged, for as long
						// as it stays open. Time-based, so a short TTS or LiveKit outage does not cost a call its tone-out.
						if (!state.FirstAttemptUtc.TryGetValue(call.CallId, out var firstAttempt))
							state.FirstAttemptUtc[call.CallId] = firstAttempt = DateTime.UtcNow;
						else if (DateTime.UtcNow - firstAttempt > giveUpAfter)
						{
							logger.Error("Giving up on toning out call {CallId}: it has failed since {FirstAttempt:O}", call.CallId, firstAttempt);
							seen.Add(call.CallId);
							state.FirstAttemptUtc.Remove(call.CallId);
							continue;
						}

						// Publishing into a channel that is reconnecting or gone loses the announcement while it would be marked
						// done; hold it (unseen) until the channel is back. The rest of this poll would wait for the same thing.
						if (!monitor.IsUsable)
						{
							logger.Warning("LiveKit channel '{Channel}' is not connected; holding the tone-out for call {CallId} until it is", channel.Name, call.CallId);
							break;
						}

						var text = VoiceModeRuntime.FormatCallAnnouncement(call, priorityNames);
						logger.Information("Toning out new call {CallId}", call.CallId);
						// Build (TTS synthesis) and publish (LiveKit) are tracked separately so a failure is
						// attributed to the right subsystem instead of always blaming TTS. AnnounceAsync does
						// both internally, so call its public BuildAnnouncementAsync step explicitly here.
						short[] audio;
						try
						{
							audio = await service.BuildAnnouncementAsync(text, cancellationToken).ConfigureAwait(false);
							// A successful build means a real TTS synthesis call reached the service.
							if (status != null)
								status.Tts = ConnectionState.Connected;
						}
						catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
						{
							throw;
						}
						catch (Exception ex)
						{
							// TTS synthesis failed (an HttpClient timeout surfaces as a TaskCanceledException, so it lands here too) → mark TTS unreachable. The call stays unseen and is
							// retried on the next poll; LiveKit status is untouched (it's not the fault).
							if (status != null)
								status.Tts = ConnectionState.Disconnected;
							logger.Error(ex, "TTS synthesis failed for call {CallId}; will retry next poll", call.CallId);
							continue;
						}

						// Synthesis can take seconds: check the channel again right before publishing.
						if (!monitor.IsUsable)
						{
							logger.Warning("LiveKit channel '{Channel}' dropped while preparing the tone-out for call {CallId}; holding it until the channel is back", channel.Name, call.CallId);
							break;
						}

						var interruptionsBefore = monitor.Interruptions;
						try
						{
							// CaptureFrameAsync back-pressures on the AudioSource queue, so this paces
							// roughly in real time as the audio is transmitted.
							await publisher.WriteAsync(audio, cancellationToken).ConfigureAwait(false);
							await publisher.FlushAsync(cancellationToken).ConfigureAwait(false);
						}
						catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
						{
							throw;
						}
						catch (Exception ex)
						{
							// Publish failed → this is a LiveKit fault, not TTS (synthesis already
							// succeeded above). Reflect it on LiveKit so the operator looks in the right
							// place; the call stays unseen and is retried on the next poll.
							if (status != null)
								status.LiveKit = ConnectionState.Disconnected;
							logger.Error(ex, "Failed to publish tone-out for call {CallId}; will retry next poll", call.CallId);
							continue;
						}

						// Only a tone-out that went out over a connection that stayed up the whole time is done; one that overlapped
						// a reconnect or a drop was probably (partly) lost, and a repeated announcement beats a missing one.
						if (monitor.Interruptions != interruptionsBefore || !monitor.IsUsable)
						{
							logger.Warning("LiveKit channel '{Channel}' was interrupted while toning out call {CallId}; it will be announced again", channel.Name, call.CallId);
							break;
						}

						// Publish reached LiveKit (recover the pill if a prior publish had failed).
						if (status != null)
							status.LiveKit = ConnectionState.Connected;
						// Only mark the call handled once the whole tone-out succeeds, so a failed
						// publish is retried on the next poll instead of being lost.
						seen.Add(call.CallId);
						state.FirstAttemptUtc.Remove(call.CallId);
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					// Includes an API timeout (TaskCanceledException without our token being cancelled): that is a failed poll,
					// not a shutdown, and must not end tone-out while the service keeps reporting itself healthy.
					logger.Error(ex, "Dispatch tone-out poll failed");
				}

				// Wake promptly on a hard disconnect instead of sleeping out the full poll interval.
				try { await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken), monitor.Disconnected).ConfigureAwait(false); }
				catch (TaskCanceledException) { break; }
			}
			}
			finally
			{
				if (publisher != null)
					await publisher.DisposeAsync().ConfigureAwait(false);
			}

			return 0;
		}

		/// <summary>True for a scheduled call: Resgrid lists it as active before its dispatch time, which is still ahead.</summary>
		internal static bool IsScheduledForLater(CallResultData call, DateTime nowUtc) =>
			call?.DispatchedOnUtc != null && DateTime.SpecifyKind(call.DispatchedOnUtc.Value, DateTimeKind.Utc) > nowUtc;

		/// <summary>
		/// Records the calls already open when the relay first starts as handled, so they are not announced. Scheduled calls
		/// (dispatch time still ahead) are left out: priming them marked them seen, so they were never toned out when they were
		/// actually dispatched.
		/// </summary>
		internal static void PrimeBacklog(IEnumerable<CallResultData> calls, DispatchToneOutState state, DateTime nowUtc)
		{
			foreach (var call in calls ?? Enumerable.Empty<CallResultData>())
			{
				if (call == null || string.IsNullOrWhiteSpace(call.CallId) || IsScheduledForLater(call, nowUtc))
					continue;
				state.Announced.Add(call.CallId);
			}
			state.Primed = true;
		}

		private static string FirstNonEmpty(params string[] values) =>
			values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

		/// <summary>The department's priority names by id; empty (default names are spoken) when they cannot be read.</summary>
		private static async Task<IReadOnlyDictionary<int, string>> LoadPriorityNamesAsync(IResgridCallPrioritiesApi api, ILogger logger, CancellationToken cancellationToken)
		{
			try
			{
				var priorities = await api.GetAllCallPrioritiesAsync(cancellationToken).ConfigureAwait(false);
				var names = new Dictionary<int, string>();
				foreach (var priority in priorities.Where(x => x != null && !x.IsDeleted && !string.IsNullOrWhiteSpace(x.Name)))
					names[priority.Id] = priority.Name.Trim();

				return names;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				logger.Warning(ex, "Could not load the department's call priorities; announcing the default priority names");
				return new Dictionary<int, string>();
			}
		}
	}

	/// <summary>
	/// Which calls the tone-out mode has already handled. Owned by the relay service so it outlives a single run: the
	/// resilience layer restarts <see cref="DispatchVoiceMode.RunAsync"/> after a disconnect, and that restart must carry on
	/// from where the last run stopped rather than treat every call created during the outage as old backlog.
	/// </summary>
	public sealed class DispatchToneOutState
	{
		public HashSet<string> Announced { get; } = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>True once the calls that were open when the relay started have been recorded as already handled.</summary>
		public bool Primed { get; set; }

		/// <summary>When the relay first tried to tone out each call that has not been announced yet.</summary>
		public Dictionary<string, DateTime> FirstAttemptUtc { get; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);
	}
}
