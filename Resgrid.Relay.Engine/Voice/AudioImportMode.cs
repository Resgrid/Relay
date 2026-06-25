#if NET10_0_WINDOWS
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Providers.ApiClient.V4;
using Serilog;

namespace Resgrid.Relay.Engine.Voice
{
	/// <summary>
	/// 'audio' mode (Windows): watches a Windows audio device for dispatch tones and
	/// creates Resgrid calls when a watcher triggers. Extracted from the console host so
	/// it can be driven by the relay service host. Windows-only because it depends on
	/// NAudio capture (via <see cref="Resgrid.Audio.Core"/>, referenced only under
	/// net10.0-windows).
	/// </summary>
	public static class AudioImportMode
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			WriteIndented = true
		};

		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, RelayStatus status = null)
		{
			var config = LoadAudioConfig(options.AudioConfigPath);
			var apiOptions = ResolveResgridOptions(options, config);
			using var apiClient = new ResgridV4ApiClient(apiOptions);
			var healthApi = new HealthApi(apiClient);
			var callsApi = new CallsApi(apiClient);

			// Client built — health is verified below before reporting Connected.
			if (status != null)
				status.ResgridApi = ConnectionState.Connecting;

			var audioStorage = new WatcherAudioStorage(logger);
			var evaluator = new AudioEvaluator(logger);
			var recorder = new AudioRecorder(evaluator, audioStorage);
			var processor = new AudioProcessor(recorder, evaluator, audioStorage);
			var comService = new ComService(logger, processor, apiClient, healthApi, callsApi);

			evaluator.WatcherTriggered += (_, eventArgs) =>
				logger.Information("WATCHER TRIGGERED: {Watcher}", eventArgs.Watcher.Name);

			comService.CallCreatedEvent += (_, eventArgs) =>
			{
				logger.Information("CALL CREATED: {CallId} ({CallNumber})", eventArgs.CallId, eventArgs.CallNumber);
				if (status != null)
					status.IncrementCallsCreated();
			};

			comService.Init(config);
			if (!comService.IsConnectionValid())
			{
				if (status != null)
					status.ResgridApi = ConnectionState.Disconnected;
				logger.Error("Unable to reach the Resgrid v4 API with the configured OpenID Connect settings.");
				return 1;
			}

			// Health check passed — the Resgrid API is reachable.
			if (status != null)
				status.ResgridApi = ConnectionState.Connected;

			logger.Information("Listening for dispatches on device {InputDevice}", config.InputDevice);
			processor.Init(config);
			processor.Start();

			// The recorder's sample aggregator publishes the peak dBFS of each capture
			// block — surface it as the live input level so the UI meter can move.
			if (status != null && recorder.SampleAggregator != null)
				recorder.SampleAggregator.MaximumCalculated += (_, eventArgs) => status.InputDbfs = eventArgs.Db;

			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			try
			{
				while (!linkedCts.IsCancellationRequested &&
					   (recorder.RecordingState == RecordingState.Monitoring ||
						recorder.RecordingState == RecordingState.Recording ||
						recorder.RecordingState == RecordingState.RequestedStop))
				{
					await Task.Delay(250, linkedCts.Token).ConfigureAwait(false);
				}
			}
			catch (TaskCanceledException)
			{
			}
			finally
			{
				recorder.Stop();
			}

			return 0;
		}

		private static Config LoadAudioConfig(string path)
		{
			var payload = File.ReadAllText(path);
			var config = JsonSerializer.Deserialize<Config>(payload, JsonOptions) ?? new Config();
			if (config.Resgrid == null)
				config.Resgrid = new ResgridConnectionSettings();
			if (config.DispatchMapping == null)
				config.DispatchMapping = new DispatchMappingSettings();

			return config;
		}

		private static ResgridApiClientOptions ResolveResgridOptions(RelayHostOptions hostOptions, Config config)
		{
			return new ResgridApiClientOptions
			{
				BaseUrl = FirstNonEmpty(hostOptions.Resgrid.BaseUrl, config.Resgrid?.BaseUrl, config.ApiUrl, "https://api.resgrid.com"),
				ApiVersion = FirstNonEmpty(hostOptions.Resgrid.ApiVersion, config.Resgrid?.ApiVersion, "4"),
				ClientId = FirstNonEmpty(hostOptions.Resgrid.ClientId, config.Resgrid?.ClientId),
				ClientSecret = FirstNonEmpty(hostOptions.Resgrid.ClientSecret, config.Resgrid?.ClientSecret),
				RefreshToken = FirstNonEmpty(hostOptions.Resgrid.RefreshToken, config.Resgrid?.RefreshToken),
				Scope = FirstNonEmpty(hostOptions.Resgrid.Scope, config.Resgrid?.Scope, "openid profile email offline_access mobile"),
				TokenCachePath = FirstNonEmpty(hostOptions.Resgrid.TokenCachePath, config.Resgrid?.TokenCachePath, Path.Combine(AppContext.BaseDirectory, "data", "resgrid-token.json"))
			};
		}

		private static string FirstNonEmpty(params string[] values) =>
			values.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x));
	}
}
#endif
