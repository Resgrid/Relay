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
			var recorder = new AudioRecorder(evaluator, audioStorage, logger);
			var processor = new AudioProcessor(recorder, evaluator, audioStorage, logger);
			var comService = new ComService(logger, processor, apiClient, healthApi, callsApi, new LookupsApi(apiClient), apiOptions.DepartmentId);

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

			// The recorder's sample aggregator publishes the peak dBFS of each capture
			// block — surface it as the live input level so the UI meter can move.
			if (status != null && recorder.SampleAggregator != null)
				recorder.SampleAggregator.MaximumCalculated += (_, eventArgs) => status.InputDbfs = eventArgs.Db;

			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			try
			{
				// Inside the try: a bad InputDevice throws from Start after Init enabled the processing timer, which then leaked
				// (with any opened capture handle) because the finally never ran.
				processor.Init(config);
				processor.Start();

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
				try
				{
					recorder.Stop();
				}
				finally
				{
					// Nested: a capture device that fails to stop must not skip this and leave the processing timer running
					// against the disposed API client.
					processor.Stop();

					// Stop waits only so long for a dispatch already being sent; keep the API client (disposed when this method
					// returns) alive until it finishes, or a call Resgrid had already saved lost its audio.
					await processor.WaitForInFlightDispatchAsync().ConfigureAwait(false);
				}
			}

			// The loop also ends when capture stops on its own (the input device was unplugged or failed). That is not a clean
			// stop: fault so the service shows it instead of reporting Stopped while no dispatch is being listened for.
			if (!cancellationToken.IsCancellationRequested)
				throw new InvalidOperationException($"Audio capture on device {config.InputDevice} stopped unexpectedly; no dispatches are being monitored.");

			return 0;
		}

		private static Config LoadAudioConfig(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				throw new RelayConfigurationException(
					$"The audio watcher file '{path}' does not exist. Create it with 'Resgrid.Audio.Relay.Console setup' or set AudioConfigPath.");

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
			// The host BaseUrl is never blank (appsettings.json and the options default both carry the public API), so a host value
			// equal to that default is treated as unset: otherwise it always beat a custom server set in settings.json by `setup`.
			var defaultBaseUrl = new ResgridApiClientOptions().BaseUrl;
			var hostBaseUrl = IsSameUrl(hostOptions.Resgrid.BaseUrl, defaultBaseUrl) ? null : hostOptions.Resgrid.BaseUrl;

			return new ResgridApiClientOptions
			{
				BaseUrl = FirstNonEmpty(hostBaseUrl, config.Resgrid?.BaseUrl, config.ApiUrl, defaultBaseUrl, "https://api.resgrid.com"),
				ApiVersion = FirstNonEmpty(hostOptions.Resgrid.ApiVersion, config.Resgrid?.ApiVersion, "4"),
				ClientId = FirstNonEmpty(hostOptions.Resgrid.ClientId, config.Resgrid?.ClientId),
				ClientSecret = FirstNonEmpty(hostOptions.Resgrid.ClientSecret, config.Resgrid?.ClientSecret),
				RefreshToken = FirstNonEmpty(hostOptions.Resgrid.RefreshToken, config.Resgrid?.RefreshToken),
				Scope = FirstNonEmpty(hostOptions.Resgrid.Scope, config.Resgrid?.Scope, "openid profile email offline_access mobile"),
				TokenCachePath = FirstNonEmpty(hostOptions.Resgrid.TokenCachePath, config.Resgrid?.TokenCachePath, Path.Combine(AppContext.BaseDirectory, "data", "resgrid-token.json")),
				// settings.json predates these, so they only come from the host options; dropping them silently forced every audio
				// relay onto the refresh_token grant even when it was configured for client credentials or a system API key.
				GrantType = hostOptions.Resgrid.GrantType,
				SystemApiKey = hostOptions.Resgrid.SystemApiKey,
				DepartmentId = hostOptions.Resgrid.DepartmentId
			};
		}

		private static string FirstNonEmpty(params string[] values) =>
			values.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x));

		private static bool IsSameUrl(string a, string b) =>
			!String.IsNullOrWhiteSpace(a) && !String.IsNullOrWhiteSpace(b) &&
			String.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
	}
}
#endif
