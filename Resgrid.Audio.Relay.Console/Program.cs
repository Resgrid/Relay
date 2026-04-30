using Microsoft.Extensions.Configuration;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Audio.Relay.Console.Smtp;
using Resgrid.Providers.ApiClient.V4;
using Serilog;
using Serilog.Core;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cli = System.Console;
#if NET10_0_WINDOWS
using NAudio.Wave;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
#endif

namespace Resgrid.Audio.Relay.Console
{
	public static class Program
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			WriteIndented = true
		};

		public static async Task<int> Main(string[] args)
		{
			var command = args.Length == 0 ? "run" : args[0].Trim().ToLowerInvariant();

			switch (command)
			{
				case "run":
					return await RunAsync().ConfigureAwait(false);
				case "setup":
					return await SetupAsync().ConfigureAwait(false);
				case "devices":
					return ShowDevices();
				case "monitor":
					return await MonitorAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
				case "version":
				case "--version":
				case "-v":
					ShowVersion();
					return 0;
				case "help":
				case "--help":
				case "-h":
					ShowHelp();
					return 0;
				default:
					Cli.Error.WriteLine($"Unknown command '{command}'.");
					ShowHelp();
					return 1;
			}
		}

		private static async Task<int> RunAsync()
		{
			var hostOptions = LoadHostOptions();
			var mode = (hostOptions.Mode ?? "smtp").Trim().ToLowerInvariant();

			// Validate required settings before starting (fail fast with clear messages).
			if (!ValidateOptions(hostOptions))
				return 1;

			using var cancellationTokenSource = new CancellationTokenSource();
			ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				cancellationTokenSource.Cancel();
			};
			Cli.CancelKeyPress += cancelHandler;

			try
			{
				switch (mode)
				{
					case "smtp":
					{
						var smtpLogger = CreateLogger(debug: false);
						await using var telemetry = SmtpTelemetry.Create(hostOptions, smtpLogger);
						ResgridV4ApiClient.Init(hostOptions.Resgrid);
						await SmtpRelayRunner.RunAsync(hostOptions.Smtp, telemetry, cancellationTokenSource.Token).ConfigureAwait(false);
						return 0;
					}
					case "audio":
						return await RunAudioModeAsync(hostOptions, cancellationTokenSource.Token).ConfigureAwait(false);
					default:
						Cli.Error.WriteLine($"Unsupported relay mode '{hostOptions.Mode}'. Supported modes are 'smtp' and 'audio'.");
						return 1;
				}
			}
			finally
			{
				Cli.CancelKeyPress -= cancelHandler;
			}
		}

		private static RelayHostOptions LoadHostOptions()
		{
			var configuration = new ConfigurationBuilder()
				.SetBasePath(AppContext.BaseDirectory)
				.AddJsonFile("appsettings.json", optional: true)
				.AddEnvironmentVariables("RESGRID__RELAY__")
				.Build();

			var options = new RelayHostOptions();
			configuration.Bind(options);

			if (!Path.IsPathRooted(options.AudioConfigPath))
				options.AudioConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.AudioConfigPath));

			if (!String.IsNullOrWhiteSpace(options.Resgrid.TokenCachePath) && !Path.IsPathRooted(options.Resgrid.TokenCachePath))
				options.Resgrid.TokenCachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.Resgrid.TokenCachePath));

			return options;
		}

		private static bool ValidateOptions(RelayHostOptions options)
		{
			var errors = new System.Collections.Generic.List<string>();

			if (String.IsNullOrWhiteSpace(options.Resgrid.BaseUrl))
				errors.Add("RESGRID__RELAY__Resgrid__BaseUrl is required. Set it to your Resgrid API URL (e.g. https://api.resgrid.com).");

			if (String.IsNullOrWhiteSpace(options.Resgrid.ClientId))
				errors.Add("RESGRID__RELAY__Resgrid__ClientId is required. Set it to your OIDC client ID.");

			if (String.IsNullOrWhiteSpace(options.Resgrid.ClientSecret))
				errors.Add("RESGRID__RELAY__Resgrid__ClientSecret is required. Set it to your OIDC client secret.");

			var grantType = options.Resgrid.GrantType;

			switch (grantType)
			{
				case ResgridAuthGrantType.RefreshToken:
					if (String.IsNullOrWhiteSpace(options.Resgrid.RefreshToken))
						errors.Add("RESGRID__RELAY__Resgrid__RefreshToken is required when GrantType is RefreshToken.");
					break;
				case ResgridAuthGrantType.SystemApiKey:
					if (String.IsNullOrWhiteSpace(options.Resgrid.SystemApiKey))
						errors.Add("RESGRID__RELAY__Resgrid__SystemApiKey is required when GrantType is SystemApiKey.");
					break;
			}

			if (options.Mode == "smtp")
			{
				var hasDomains = (options.Smtp.DepartmentAddressDomains?.Length ?? 0) > 0
					|| (options.Smtp.GroupAddressDomains?.Length ?? 0) > 0
					|| (options.Smtp.GroupMessageAddressDomains?.Length ?? 0) > 0
					|| (options.Smtp.ListAddressDomains?.Length ?? 0) > 0;

				if (!hasDomains)
					errors.Add("At least one dispatch domain must be configured. Set RESGRID__RELAY__Smtp__DepartmentAddressDomains__0, RESGRID__RELAY__Smtp__GroupAddressDomains__0, etc.");
			}

			if (errors.Count > 0)
			{
				foreach (var error in errors)
					Cli.Error.WriteLine($"Configuration error: {error}");

				Cli.Error.WriteLine();
				Cli.Error.WriteLine("All settings are driven by environment variables prefixed with RESGRID__RELAY__.");
				Cli.Error.WriteLine("Run 'Resgrid.Audio.Relay.Console help' for the full list.");
				return false;
			}

			return true;
		}

		private static void ShowVersion()
		{
			var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
			Cli.WriteLine($"Resgrid Relay {version}");
		}

		private static void ShowHelp()
		{
			Cli.WriteLine("Resgrid Relay");
			Cli.WriteLine("------------------------------");
			Cli.WriteLine("Commands:");
			Cli.WriteLine("  run       Starts the relay in the configured mode (default)");
			Cli.WriteLine("  setup     Creates or updates the Windows audio settings.json file");
			Cli.WriteLine("  devices   Lists Windows audio input devices");
			Cli.WriteLine("  monitor   Monitors a Windows audio device without dispatching calls");
			Cli.WriteLine("  version   Prints the application version");
			Cli.WriteLine();
			Cli.WriteLine("Environment variables:");
			Cli.WriteLine("  RESGRID__RELAY__Mode=smtp|audio");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__ClientId=...");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__ClientSecret=...");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__RefreshToken=...");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__GrantType=RefreshToken|ClientCredentials|SystemApiKey");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__SystemApiKey=...");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__DepartmentId=...");
			Cli.WriteLine("  RESGRID__RELAY__Telemetry__Sentry__Dsn=...");
			Cli.WriteLine("  RESGRID__RELAY__Telemetry__Countly__Url=https://countly.example.com");
			Cli.WriteLine("  RESGRID__RELAY__Telemetry__Countly__AppKey=...");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__Port=2525");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__HostedMode=false");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__DefaultDepartmentId=...");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__ResolveDispatchCodes=true|false");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__DepartmentAddressDomains__0=dispatch.resgrid.com");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__GroupAddressDomains__0=groups.resgrid.com");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__GroupMessageAddressDomains__0=gm.resgrid.com");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__ListAddressDomains__0=lists.resgrid.com");
		}

#if NET10_0_WINDOWS
		private static async Task<int> RunAudioModeAsync(RelayHostOptions hostOptions, CancellationToken cancellationToken)
		{
			var config = LoadAudioConfig(hostOptions.AudioConfigPath);
			var apiOptions = ResolveResgridOptions(hostOptions, config);
			ResgridV4ApiClient.Init(apiOptions);

			Logger logger = CreateLogger(config.Debug);
			var audioStorage = new WatcherAudioStorage(logger);
			var evaluator = new AudioEvaluator(logger);
			var recorder = new AudioRecorder(evaluator, audioStorage);
			var processor = new AudioProcessor(recorder, evaluator, audioStorage);
			var comService = new ComService(logger, processor);

			evaluator.WatcherTriggered += (_, eventArgs) =>
				Cli.WriteLine($"{DateTime.Now:G}: WATCHER TRIGGERED: {eventArgs.Watcher.Name}");

			comService.CallCreatedEvent += (_, eventArgs) =>
				Cli.WriteLine($"{eventArgs.Timestamp:G}: CALL CREATED: {eventArgs.CallId} ({eventArgs.CallNumber})");

			comService.Init(config);
			if (!comService.IsConnectionValid())
			{
				Cli.Error.WriteLine("Unable to reach the Resgrid v4 API with the configured OpenID Connect settings.");
				return 1;
			}

			Cli.WriteLine($"Listening for dispatches on device {config.InputDevice}");
			processor.Init(config);
			processor.Start();

			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			ConsoleCancelEventHandler stopHandler = (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				recorder.Stop();
				linkedCts.Cancel();
			};
			Cli.CancelKeyPress += stopHandler;

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
				Cli.CancelKeyPress -= stopHandler;
			}

			return 0;
		}

		private static async Task<int> SetupAsync()
		{
			Cli.WriteLine("Resgrid Relay Audio Setup");
			Cli.WriteLine("------------------------------");

			var config = File.Exists(DefaultAudioConfigPath())
				? LoadAudioConfig(DefaultAudioConfigPath())
				: new Config();

			config.Resgrid.BaseUrl = Prompt("Resgrid API Url", config.Resgrid.BaseUrl);
			config.Resgrid.ClientId = Prompt("OIDC Client Id", config.Resgrid.ClientId);
			config.Resgrid.ClientSecret = Prompt("OIDC Client Secret", config.Resgrid.ClientSecret);
			config.Resgrid.RefreshToken = Prompt("OIDC Refresh Token", config.Resgrid.RefreshToken);

			ShowDevices();
			config.InputDevice = Int32.Parse(Prompt("Audio device number", config.InputDevice.ToString()));
			config.AudioLength = Int32.Parse(Prompt("Recording length in seconds", config.AudioLength.ToString()));
			config.Multiple = Prompt("Create separate calls per watcher? (y/n)", config.Multiple ? "y" : "n").StartsWith("y", StringComparison.OrdinalIgnoreCase);

			var watcherCount = Int32.Parse(Prompt("How many groups or department watchers?", config.Watchers.Count == 0 ? "1" : config.Watchers.Count.ToString()));
			config.Watchers.Clear();
			for (var i = 0; i < watcherCount; i++)
			{
				config.Watchers.Add(CreateWatcher(i + 1));
			}

			SaveAudioConfig(DefaultAudioConfigPath(), config);
			Cli.WriteLine($"Settings written to {DefaultAudioConfigPath()}");
			await Task.CompletedTask.ConfigureAwait(false);
			return 0;
		}

		private static Watcher CreateWatcher(int index)
		{
			Cli.WriteLine();
			Cli.WriteLine($"Watcher #{index}");
			Cli.WriteLine("------------------------------");

			var watcher = new Watcher
			{
				Id = Guid.NewGuid(),
				Active = true,
				Name = Prompt("Watcher Name", $"Watcher {index}"),
				Code = Prompt("Watcher Group or Department Dispatch Code", String.Empty),
				Type = Prompt("Is this a department dispatch code? (y/n)", "n").StartsWith("y", StringComparison.OrdinalIgnoreCase)
					? 1
					: 2,
				AdditionalCodes = Prompt("Additional Group Dispatch Codes (comma separated)", String.Empty)
			};

			watcher.Triggers = new System.Collections.Generic.List<Trigger>
			{
				new Trigger
				{
					Count = Int32.Parse(Prompt("How many tones for this watcher (1 or 2)", "2")),
					Frequency1 = Double.Parse(Prompt("Tone 1 frequency", "524")),
					Time1 = Int32.Parse(Prompt("Tone 1 length in milliseconds", "500"))
				}
			};

			if (watcher.Triggers[0].Count >= 2)
			{
				watcher.Triggers[0].Frequency2 = Double.Parse(Prompt("Tone 2 frequency", "794"));
				watcher.Triggers[0].Time2 = Int32.Parse(Prompt("Tone 2 length in milliseconds", "500"));
			}

			return watcher;
		}

		private static int ShowDevices()
		{
			for (var waveInDevice = 0; waveInDevice < WaveIn.DeviceCount; waveInDevice++)
			{
				var deviceInfo = WaveIn.GetCapabilities(waveInDevice);
				Cli.WriteLine($"Device {waveInDevice}: {deviceInfo.ProductName}, {deviceInfo.Channels} channels");
			}

			return 0;
		}

		private static async Task<int> MonitorAsync(string[] args)
		{
			var device = args.Length > 0 ? Int32.Parse(args[0]) : 0;
			Logger logger = CreateLogger(debug: true);
			var audioStorage = new WatcherAudioStorage(logger);
			var evaluator = new AudioEvaluator(logger);
			var recorder = new AudioRecorder(evaluator, audioStorage);
			recorder.SetSampleAggregator(new SampleAggregator());
			recorder.SampleAggregator.MaximumCalculated += (_, eventArgs) =>
				Cli.WriteLine($"{DateTime.Now:G}: Min={eventArgs.MinSample} Max={eventArgs.MaxSample} dB={eventArgs.Db}");

			recorder.BeginMonitoring(device);
			Cli.WriteLine("Monitoring audio. Press Ctrl+C to stop.");

			using var cancellationTokenSource = new CancellationTokenSource();
			ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				recorder.Stop();
				cancellationTokenSource.Cancel();
			};
			Cli.CancelKeyPress += cancelHandler;

			try
			{
				while (!cancellationTokenSource.IsCancellationRequested &&
					   (recorder.RecordingState == RecordingState.Monitoring ||
						recorder.RecordingState == RecordingState.Recording ||
						recorder.RecordingState == RecordingState.RequestedStop))
				{
					await Task.Delay(250, cancellationTokenSource.Token).ConfigureAwait(false);
				}
			}
			catch (TaskCanceledException)
			{
			}
			finally
			{
				Cli.CancelKeyPress -= cancelHandler;
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

		private static void SaveAudioConfig(string path, Config config)
		{
			var directory = Path.GetDirectoryName(path);
			if (!String.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
		}

		private static string DefaultAudioConfigPath()
		{
			return Path.Combine(AppContext.BaseDirectory, "settings.json");
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

		private static string Prompt(string text, string defaultValue)
		{
			Cli.Write($"{text}{(String.IsNullOrWhiteSpace(defaultValue) ? String.Empty : $" [{defaultValue}]")}: ");
			var input = Cli.ReadLine();
			return String.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
		}
#else
		private static Task<int> RunAudioModeAsync(RelayHostOptions hostOptions, CancellationToken cancellationToken)
		{
			Cli.Error.WriteLine("Audio mode is only available in the net10.0-windows build.");
			return Task.FromResult(1);
		}

		private static Task<int> SetupAsync()
		{
			Cli.Error.WriteLine("Audio setup is only available in the net10.0-windows build.");
			return Task.FromResult(1);
		}

		private static int ShowDevices()
		{
			Cli.Error.WriteLine("Audio device enumeration is only available in the net10.0-windows build.");
			return 1;
		}

		private static Task<int> MonitorAsync(string[] args)
		{
			Cli.Error.WriteLine("Audio monitoring is only available in the net10.0-windows build.");
			return Task.FromResult(1);
		}
#endif

		private static Logger CreateLogger(bool debug)
		{
			return new LoggerConfiguration()
				.MinimumLevel.Is(debug ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
				.WriteTo.Console()
				.CreateLogger();
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x));
		}
	}
}
