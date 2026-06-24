using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Audio.Voice;
using Resgrid.Audio.Voice.Connection;
using Resgrid.Audio.Voice.LiveKit;
using Resgrid.Audio.Voice.ToneOut;
using Resgrid.Providers.ApiClient.V4;
using Serilog;
using Cli = System.Console;

namespace Resgrid.Audio.Relay.Console.Voice
{
	/// <summary>
	/// 'dispatch' mode: watches for new Resgrid calls and tones them out (alert tones +
	/// Resgrid TTS announcement) onto a department's PTT channel. Cross-platform.
	/// Customer single-department now; hosted multi-department is scaffolded.
	/// </summary>
	internal static class DispatchVoiceMode
	{
		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(options.Tts.ServiceBaseUrl))
			{
				Cli.Error.WriteLine("Dispatch tone-out requires RESGRID__RELAY__Tts__ServiceBaseUrl (the Resgrid TTS service URL).");
				return 1;
			}

			ResgridV4ApiClient.Init(options.Resgrid);

			var deptId = FirstNonEmpty(options.DispatchVoice.DepartmentId, options.Voice.DepartmentId);
			if (options.DispatchVoice.Hosted && string.IsNullOrWhiteSpace(deptId))
			{
				Cli.Error.WriteLine("Hosted dispatch tone-out requires RESGRID__RELAY__DispatchVoice__DepartmentId.");
				return 1;
			}

			var transport = new LiveKitVoiceTransport(logger, options.Voice.PublishQueueMs);
			var provider = new ResgridVoiceChannelProvider(logger);
			await using var manager = new VoiceRoomManager(transport, logger);

			var channel = await provider.GetChannelAsync(options.DispatchVoice.Channel, deptId, cancellationToken).ConfigureAwait(false);
			var session = await manager.JoinAsync(channel, cancellationToken).ConfigureAwait(false);
			var publisher = await session.CreatePublisherAsync("dispatch", cancellationToken).ConfigureAwait(false);

			using var tts = new ResgridTtsClient(options.Tts, logger);
			var service = new DispatchToneOutService(tts, new ToneGenerator(), options.DispatchVoice.Tone, logger);

			// Prime "seen" with the current backlog so startup doesn't re-announce open calls.
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var call in await CallsApi.GetActiveCallsAsync(deptId, cancellationToken).ConfigureAwait(false))
				if (!string.IsNullOrWhiteSpace(call.CallId))
					seen.Add(call.CallId);

			var pollSeconds = Math.Max(5, options.DispatchVoice.PollSeconds);
			Cli.WriteLine($"Dispatch tone-out on '{channel.Name}', polling new calls every {pollSeconds}s. Press Ctrl+C to stop.");

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					var calls = await CallsApi.GetActiveCallsAsync(deptId, cancellationToken).ConfigureAwait(false);
					foreach (var call in calls)
					{
						if (string.IsNullOrWhiteSpace(call.CallId) || !seen.Add(call.CallId))
							continue;

						var text = VoiceModeRuntime.FormatCallAnnouncement(call);
						logger.Information("Toning out new call {CallId}", call.CallId);
						await service.AnnounceAsync(publisher, text, cancellationToken).ConfigureAwait(false);
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
