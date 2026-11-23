using Microsoft.Extensions.Configuration;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Services;
using Resgrid.Providers.ApiClient.V4;
using Serilog;
using Serilog.Core;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cli = System.Console;
using EngineVoice = Resgrid.Relay.Engine.Voice;
#if NET10_0_WINDOWS
using NAudio.Wave;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using System.IO.Ports;
using Cm108Compatibility = Resgrid.Audio.Core.Radio.Cm108Compatibility;
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

		/// <summary>
		/// Exit code for "the configuration is wrong" (sysexits EX_CONFIG). Distinct from 1 so service managers can stop
		/// restarting a relay that can never start until someone fixes its settings (the systemd unit sets
		/// RestartPreventExitStatus=78).
		/// </summary>
		public const int ConfigurationErrorExitCode = 78;

		public static async Task<int> Main(string[] args)
		{
			var command = args.Length == 0 ? "run" : args[0].Trim().ToLowerInvariant();

			try
			{
				return await RunCommandAsync(command, args).ConfigureAwait(false);
			}
			catch (RelayConfigurationException ex)
			{
				// A setting that can't be bound (e.g. RESGRID__RELAY__Smtp__Port= or a GrantType typo) used to crash with a stack
				// trace, which under systemd became an endless restart loop.
				Cli.Error.WriteLine($"Configuration error: {ex.Message}");
				return ConfigurationErrorExitCode;
			}
			catch (RelaySettingsUnavailableException ex)
			{
				// Locked or unreadable settings file: an ordinary failure (exit 1), so systemd's Restart=on-failure tries again
				// once the lock clears. A file that stays unreadable keeps failing with this message in the journal.
				Cli.Error.WriteLine($"Settings unavailable: {ex.Message}");
				return 1;
			}
		}

		private static async Task<int> RunCommandAsync(string command, string[] args)
		{
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
				case "tune":
					return await TuneAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
				case "profiles":
					return Profiles(args.Skip(1).ToArray());
				case "test-tx":
					return await TestTransmitAsync().ConfigureAwait(false);
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

			// Validate required settings before starting (fail fast with clear messages).
			if (!ValidateOptions(hostOptions))
				return ConfigurationErrorExitCode;

			// Optional LocalXpose tunnel (replaces the old shell entrypoint logic so the
			// container can run on a shell-less hardened base image).
			using var loclxTunnel = LoclxTunnel.StartIfEnabled(hostOptions);

			using var cancellationTokenSource = new CancellationTokenSource();
			ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				cancellationTokenSource.Cancel();
			};
			Cli.CancelKeyPress += cancelHandler;

			// Also shut down gracefully on SIGTERM — the default signal `systemctl stop` / `docker stop`
			// send. Mirrors the Ctrl+C / SIGINT path so in-flight work (recordings, LiveKit) unwinds
			// cleanly instead of the process being killed. Disposed before the CTS (reverse using order),
			// so the handler can never fire against a disposed token source.
			using var sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
			{
				context.Cancel = true;
				cancellationTokenSource.Cancel();
			});

			try
			{
				// The engine owns all mode wiring; the console just builds the service and runs it.
				IRelayService service;
				try
				{
					service = RelayServiceFactory.Create(hostOptions, CreateLogger(debug: false));
				}
				catch (ArgumentException ex)
				{
					Cli.Error.WriteLine(ex.Message);
					return ConfigurationErrorExitCode;
				}

				await using (service)
				{
					await service.StartAsync(cancellationTokenSource.Token).ConfigureAwait(false);
				}

				if (service.State != RelayServiceState.Faulted)
					return 0;

				// StartAsync reports a fault through State rather than throwing, so the catch in Main never sees a configuration
				// error raised once the mode is running (e.g. record mode with the client_credentials grant); read it off the fault.
				return (service as RelayServiceBase)?.Fault is RelayConfigurationException ? ConfigurationErrorExitCode : 1;
			}
			finally
			{
				Cli.CancelKeyPress -= cancelHandler;
			}
		}

		/// <summary>
		/// The same layered settings the desktop app uses (lowest to highest precedence): appsettings.json next to the binary,
		/// the per-user relay.user.json the desktop app saves (%APPDATA%\Resgrid\Relay on Windows), then RESGRID__RELAY__
		/// environment variables. So a relay configured in the Windows app runs unchanged from the console, and env vars still
		/// win for containers and systemd.
		/// </summary>
		private static RelayHostOptions LoadHostOptions() => RelayConfiguration.Load();

		private static bool ValidateOptions(RelayHostOptions options)
		{
			var errors = new System.Collections.Generic.List<string>();

			// Audio mode can take its credentials from the watcher file 'setup' writes (settings.json); AudioImportMode merges
			// both and the API client validates the result, so the host-level checks would reject a working setup.
			var isAudioMode = String.Equals(options.Mode?.Trim(), "audio", StringComparison.OrdinalIgnoreCase);

			if (!isAudioMode)
			{
				if (String.IsNullOrWhiteSpace(options.Resgrid.BaseUrl))
					errors.Add("RESGRID__RELAY__Resgrid__BaseUrl is required. Set it to your Resgrid API URL (e.g. https://api.resgrid.com).");

				if (String.IsNullOrWhiteSpace(options.Resgrid.ClientId))
					errors.Add("RESGRID__RELAY__Resgrid__ClientId is required. Set it to your OIDC client ID.");

				if (String.IsNullOrWhiteSpace(options.Resgrid.ClientSecret))
					errors.Add("RESGRID__RELAY__Resgrid__ClientSecret is required. Set it to your OIDC client secret.");

				switch (options.Resgrid.GrantType)
				{
					case ResgridAuthGrantType.RefreshToken:
						if (String.IsNullOrWhiteSpace(options.Resgrid.RefreshToken) && !File.Exists(options.Resgrid.TokenCachePath ?? ""))
							errors.Add("RESGRID__RELAY__Resgrid__RefreshToken is required when GrantType is RefreshToken (or a token cache file from an earlier run).");
						break;
					case ResgridAuthGrantType.SystemApiKey:
						if (String.IsNullOrWhiteSpace(options.Resgrid.SystemApiKey))
							errors.Add("RESGRID__RELAY__Resgrid__SystemApiKey is required when GrantType is SystemApiKey.");
						break;
				}
			}

			if (String.Equals(options.Mode?.Trim(), "smtp", StringComparison.OrdinalIgnoreCase))
			{
				var hasDomains = (options.Smtp.DepartmentAddressDomains?.Length ?? 0) > 0
					|| (options.Smtp.GroupAddressDomains?.Length ?? 0) > 0
					|| (options.Smtp.GroupMessageAddressDomains?.Length ?? 0) > 0
					|| (options.Smtp.ListAddressDomains?.Length ?? 0) > 0;

				if (!hasDomains)
					errors.Add("At least one dispatch domain must be configured. Set RESGRID__RELAY__Smtp__DepartmentAddressDomains__0, RESGRID__RELAY__Smtp__GroupAddressDomains__0, etc.");

				if (options.Smtp.RedisCache is { Enabled: true } && String.IsNullOrWhiteSpace(options.Smtp.RedisCache.ConnectionString))
					errors.Add("Redis cache enabled but RESGRID__RELAY__Smtp__RedisCache__ConnectionString is not set.");
			}

			if (errors.Count > 0)
			{
				foreach (var error in errors)
					Cli.Error.WriteLine($"Configuration error: {error}");

				Cli.Error.WriteLine();
				Cli.Error.WriteLine($"Settings come from appsettings.json, {RelayConfiguration.UserConfigPath} (written by the Windows app),");
				Cli.Error.WriteLine("and RESGRID__RELAY__ environment variables (highest precedence).");
				Cli.Error.WriteLine("Run 'Resgrid.Audio.Relay.Console help' for the common settings.");
				return false;
			}

			return true;
		}

		private static int Profiles(string[] args)
		{
			if (args.Length >= 2 && String.Equals(args[0], "apply", StringComparison.OrdinalIgnoreCase))
			{
				var profile = RadioProfiles.All.FirstOrDefault(p => String.Equals(p.Key, args[1], StringComparison.OrdinalIgnoreCase));
				if (profile == null)
				{
					Cli.Error.WriteLine($"Unknown radio profile '{args[1]}'. Run 'profiles' to list them.");
					return 1;
				}

				// Patch only the keys the profile sets into the per-user file: saving the whole merged model would copy
				// appsettings.json (credentials included) into it and shadow later appsettings.json edits.
				var radio = new RadioModeOptions();
				profile.ApplyTo(radio);
				RelayConfiguration.PatchUserFile(root =>
				{
					var node = root["Radio"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
					root["Radio"] = node;
					node["Profile"] = radio.Profile;
					if (profile.AppliesSettings)
					{
						node["TxLeadMs"] = radio.TxLeadMs;
						node["TxHangMs"] = radio.TxHangMs;
						node["TxTailMs"] = radio.TxTailMs;
						node["CourtesyTone"] = radio.CourtesyTone;
						node["AntiLoop"] = radio.AntiLoop;
						node["HighPassCutoffHz"] = radio.HighPassCutoffHz;
						var squelch = node["Squelch"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
						node["Squelch"] = squelch;
						squelch["Mode"] = radio.Squelch.Mode.ToString();
						squelch["OpenDbfs"] = radio.Squelch.OpenDbfs;
						squelch["CloseDbfs"] = radio.Squelch.CloseDbfs;
						squelch["HangMs"] = radio.Squelch.HangMs;
						var emergency = node["Emergency"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
						node["Emergency"] = emergency;
						emergency["DetectMdc1200"] = radio.Emergency.DetectMdc1200;
						emergency["DetectTones"] = radio.Emergency.DetectTones;
					}
				});

				var effective = LoadHostOptions();
				if (profile.IsPushToTalkOverCellular && String.Equals(effective.Radio.PttMethod, "Vox", StringComparison.OrdinalIgnoreCase))
					Cli.WriteLine("Note: this radio needs a hard PTT line. Set Radio:PttMethod to Cm108, SerialRts or SerialDtr.");
				Cli.WriteLine($"Applied '{profile.DisplayName}' to {RelayConfiguration.UserConfigPath}:");
				Cli.WriteLine($"  TxLeadMs={effective.Radio.TxLeadMs} TxHangMs={effective.Radio.TxHangMs} TxTailMs={effective.Radio.TxTailMs} CourtesyTone={effective.Radio.CourtesyTone}");
				Cli.WriteLine($"  Squelch={effective.Radio.Squelch.Mode} Open={effective.Radio.Squelch.OpenDbfs} Close={effective.Radio.Squelch.CloseDbfs} Hang={effective.Radio.Squelch.HangMs}");
				return 0;
			}

			Cli.WriteLine("Radio profiles (apply with: profiles apply <key>):");
			foreach (var profile in RadioProfiles.All)
			{
				Cli.WriteLine();
				Cli.WriteLine($"  {profile.Key,-14} {profile.DisplayName}");
				Cli.WriteLine($"  {"",-14} {profile.Description}");
				if (profile.AppliesSettings)
					Cli.WriteLine($"  {"",-14} TxLeadMs={profile.TxLeadMs} TxHangMs={profile.TxHangMs} Squelch={profile.SquelchMode} {profile.SquelchOpenDbfs}/{profile.SquelchCloseDbfs} dBFS CourtesyTone={profile.CourtesyTone}");
				foreach (var line in Wrap(profile.SetupNotes, 100))
					Cli.WriteLine($"  {"",-14} {line}");
			}
			return 0;
		}

		private static System.Collections.Generic.IEnumerable<string> Wrap(string text, int width)
		{
			var line = new System.Text.StringBuilder();
			foreach (var word in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				if (line.Length > 0 && line.Length + word.Length + 1 > width)
				{
					yield return line.ToString();
					line.Clear();
				}
				if (line.Length > 0)
					line.Append(' ');
				line.Append(word);
			}
			if (line.Length > 0)
				yield return line.ToString();
		}

		private static void ShowVersion()
		{
			var assembly = Assembly.GetExecutingAssembly();
			var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
				?? assembly.GetName().Version?.ToString()
				?? "unknown";
			var plus = version.IndexOf('+');
			if (plus >= 0 && version.Length > plus + 8)
				version = $"{version.Substring(0, plus)} ({version.Substring(plus + 1, 7)})";
			Cli.WriteLine($"Resgrid Relay {version}");
		}

		private static void ShowHelp()
		{
			Cli.WriteLine("Resgrid Relay");
			Cli.WriteLine("------------------------------");
			Cli.WriteLine("Commands:");
			Cli.WriteLine("  run       Starts the relay in the configured mode (default)");
			Cli.WriteLine("  setup     Creates or updates the Windows audio settings.json file");
			Cli.WriteLine("  devices   Lists audio in/out devices, serial ports and CM108 HID (radio mode)");
			Cli.WriteLine("  monitor   Monitors a Windows audio device without dispatching calls");
			Cli.WriteLine("  tune      Live receive-level meter + squelch state to tune anti-static (radio mode)");
			Cli.WriteLine("  test-tx   Keys the radio and plays a 2 s test tone after the key-up lead (radio mode)");
			Cli.WriteLine("  profiles  Lists radio profiles (analog, Rapid Radios, Diga-Talk+, other LTE/PoC);");
			Cli.WriteLine("            'profiles apply <key>' writes one into the per-user settings file");
			Cli.WriteLine("  version   Prints the application version");
			Cli.WriteLine();
			Cli.WriteLine("Modes (RESGRID__RELAY__Mode):");
			Cli.WriteLine("  smtp      SMTP dispatch relay (default, cross-platform)");
			Cli.WriteLine("  audio     Windows tone-detect dispatch importer");
			Cli.WriteLine("  radio     Bidirectional radio <-> Resgrid PTT (LiveKit) bridge (Windows)");
			Cli.WriteLine("  record    Record PTT channel transmissions to disk/S3 + metadata log (any OS)");
			Cli.WriteLine("  dispatch  Tone out new calls (tones + Resgrid TTS) to a PTT channel (any OS)");
			Cli.WriteLine();
			Cli.WriteLine("Settings (lowest to highest precedence):");
			Cli.WriteLine("  appsettings.json next to this program");
			Cli.WriteLine($"  {RelayConfiguration.UserConfigPath}   (saved by the Windows app)");
			Cli.WriteLine("  RESGRID__RELAY__* environment variables, ':' written as '__'");
			Cli.WriteLine();
			Cli.WriteLine("Environment variables:");
			Cli.WriteLine("  RESGRID__RELAY__Mode=smtp|audio|radio|record|dispatch");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__BaseUrl=https://api.resgrid.com");
			Cli.WriteLine("  RESGRID__RELAY__Resgrid__TokenCachePath=./data/resgrid-token.json   (keeps the rotated refresh token)");
			Cli.WriteLine("  RESGRID__RELAY__AudioConfigPath=settings.json   (audio mode watcher file, written by 'setup')");
			Cli.WriteLine("  RESGRID__RELAY__Resilience__MaxConsecutiveFailures=5  ...__MaxBackoffSeconds=60   (voice modes)");
			Cli.WriteLine("  # Voice (radio/record/dispatch):");
			Cli.WriteLine("  RESGRID__RELAY__Voice__Channel=default|<name>|<channelId>");
			Cli.WriteLine("  RESGRID__RELAY__Voice__DepartmentId=...   (hosted/system-key)");
			Cli.WriteLine("  # Radio bridge (Windows):");
			Cli.WriteLine("  RESGRID__RELAY__Radio__InputDevice=0   RESGRID__RELAY__Radio__OutputDevice=-1");
			Cli.WriteLine("  RESGRID__RELAY__Radio__PttMethod=Vox|SerialRts|SerialDtr|Cm108");
			Cli.WriteLine("  RESGRID__RELAY__Radio__SerialPort=COM3   RESGRID__RELAY__Radio__Cm108GpioPin=3");
			Cli.WriteLine("  RESGRID__RELAY__Radio__Profile=custom|analog|rapidradios|digatalkplus|poc   (informational; see 'profiles')");
			Cli.WriteLine("  RESGRID__RELAY__Radio__TxLeadMs=0   (key-up lead; 500-800 for LTE/PoC donor radios)");
			Cli.WriteLine("  RESGRID__RELAY__Radio__TxHangMs=300  ...__TxTailMs=150  ...__CourtesyTone=true  ...__AntiLoop=true");
			Cli.WriteLine("  RESGRID__RELAY__Radio__Squelch__Mode=Vox|Carrier|Ctcss|Off");
			Cli.WriteLine("  RESGRID__RELAY__Radio__Squelch__OpenDbfs=-38  ...__CloseDbfs=-45  ...__HangMs=600");
			Cli.WriteLine("  RESGRID__RELAY__Radio__Emergency__DetectMdc1200=true  ...__DetectTones=true");
			Cli.WriteLine("  # Recorder (record mode / RecordWhileBridging):");
			Cli.WriteLine("  RESGRID__RELAY__Recorder__Channel=all|<name>   ...__Store=local|s3|both");
			Cli.WriteLine("  RESGRID__RELAY__Recorder__Log=jsonl|sqlite|none   ...__LocalPath=./recordings   ...__LogPath=(default: in LocalPath)");
			Cli.WriteLine("  RESGRID__RELAY__Recorder__S3__Bucket=...  ...__S3__Region=...  ...__S3__AccessKey=...");
			Cli.WriteLine("  # Dispatch tone-out:");
			Cli.WriteLine("  RESGRID__RELAY__Tts__ServiceBaseUrl=https://tts.resgrid.com");
			Cli.WriteLine("  RESGRID__RELAY__DispatchVoice__Channel=default   ...__PollSeconds=15");
			Cli.WriteLine();
			Cli.WriteLine("  # SMTP mode:");
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
			Cli.WriteLine("  RESGRID__RELAY__Smtp__RedisCache__Enabled=true|false");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__RedisCache__ConnectionString=redis:6379,abortConnect=false");
			Cli.WriteLine("  RESGRID__RELAY__Smtp__RedisCache__TtlMinutes=60");
		}

#if NET10_0_WINDOWS
		private static async Task<int> SetupAsync()
		{
			Cli.WriteLine("Resgrid Relay Audio Setup");
			Cli.WriteLine("------------------------------");

			// The file 'run' reads (AudioConfigPath, resolved against the app folder), not always <app>\settings.json.
			var hostOptions = LoadHostOptions();
			var configPath = String.IsNullOrWhiteSpace(hostOptions.AudioConfigPath) ? DefaultAudioConfigPath() : hostOptions.AudioConfigPath;

			var config = File.Exists(configPath)
				? LoadAudioConfig(configPath)
				: new Config();

			config.Resgrid.BaseUrl = Prompt("Resgrid API Url", String.IsNullOrWhiteSpace(config.Resgrid.BaseUrl) ? "https://api.resgrid.com" : config.Resgrid.BaseUrl);
			config.Resgrid.ClientId = Prompt("OIDC Client Id", config.Resgrid.ClientId);
			config.Resgrid.ClientSecret = PromptSecret("OIDC Client Secret", config.Resgrid.ClientSecret);
			config.Resgrid.RefreshToken = PromptSecret("OIDC Refresh Token", config.Resgrid.RefreshToken);

			ShowDevices();
			config.InputDevice = PromptInt("Audio device number", config.InputDevice, 0, 255);
			config.AudioLength = PromptInt("Recording length in seconds", config.AudioLength <= 0 ? 30 : config.AudioLength, 1, 600);
			config.Multiple = Prompt("Create separate calls per watcher? (y/n)", config.Multiple ? "y" : "n").StartsWith("y", StringComparison.OrdinalIgnoreCase);

			var watcherCount = PromptInt("How many groups or department watchers?", config.Watchers.Count == 0 ? 1 : config.Watchers.Count, 1, 100);
			config.Watchers.Clear();
			for (var i = 0; i < watcherCount; i++)
			{
				config.Watchers.Add(CreateWatcher(i + 1));
			}

			SaveAudioConfig(configPath, config);
			Cli.WriteLine($"Settings written to {configPath}");
			if (!String.Equals(hostOptions.Mode?.Trim(), "audio", StringComparison.OrdinalIgnoreCase))
			{
				Cli.WriteLine("The configured mode is not 'audio'. To run tone detection, set Mode to 'audio' (in the Windows app's");
				Cli.WriteLine("Configuration screen, appsettings.json, or RESGRID__RELAY__Mode=audio) and then run 'run'.");
			}
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
					Count = PromptInt("How many tones for this watcher (1 or 2)", 2, 1, 2),
					Frequency1 = PromptDouble("Tone 1 frequency (Hz)", 524, 100, 4000),
					Time1 = PromptInt("Tone 1 length in milliseconds", 500, 50, 10000)
				}
			};

			if (watcher.Triggers[0].Count >= 2)
			{
				// The detector works in whole hertz: two frequencies that round to the same tone can't be told apart, so one long tone
				// would look like both. Ask again rather than save a trigger that pages on the wrong pattern.
				while (true)
				{
					watcher.Triggers[0].Frequency2 = PromptDouble("Tone 2 frequency (Hz)", 794, 100, 4000);
					if (Trigger.ToneKey(watcher.Triggers[0].Frequency2) != Trigger.ToneKey(watcher.Triggers[0].Frequency1))
						break;
					Cli.WriteLine($"  Tone 2 must differ from tone 1 by at least 1 Hz (both are detected as {Trigger.ToneKey(watcher.Triggers[0].Frequency1)} Hz).");
				}
				watcher.Triggers[0].Time2 = PromptInt("Tone 2 length in milliseconds", 500, 50, 10000);
			}

			return watcher;
		}

		/// <summary>Prompts until the answer is a whole number in range (Enter keeps the default).</summary>
		private static int PromptInt(string text, int defaultValue, int min, int max)
		{
			while (true)
			{
				var answer = Prompt($"{text} ({min}-{max})", defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
				if (Int32.TryParse(answer, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
					&& value >= min && value <= max)
					return value;
				Cli.WriteLine($"  Enter a whole number from {min} to {max}.");
			}
		}

		/// <summary>Prompts until the answer is a number in range; '.' is the decimal separator on every locale.</summary>
		private static double PromptDouble(string text, double defaultValue, double min, double max)
		{
			while (true)
			{
				var answer = Prompt($"{text} ({min}-{max})", defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
				if (Double.TryParse(answer, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
					&& value >= min && value <= max)
					return value;
				Cli.WriteLine($"  Enter a number from {min} to {max} (use '.' for decimals, e.g. 368.5).");
			}
		}

		private static int ShowDevices()
		{
			Cli.WriteLine("Audio input devices (radio receive):");
			for (var waveInDevice = 0; waveInDevice < WaveIn.DeviceCount; waveInDevice++)
			{
				var deviceInfo = WaveIn.GetCapabilities(waveInDevice);
				Cli.WriteLine($"  In  {waveInDevice}: {deviceInfo.ProductName}, {deviceInfo.Channels} channels");
			}

			Cli.WriteLine("Audio output devices (radio transmit):");
			Cli.WriteLine("  Out -1: System default");
			for (var waveOutDevice = 0; waveOutDevice < WaveOut.DeviceCount; waveOutDevice++)
			{
				var deviceInfo = WaveOut.GetCapabilities(waveOutDevice);
				Cli.WriteLine($"  Out {waveOutDevice}: {deviceInfo.ProductName}, {deviceInfo.Channels} channels");
			}

			Cli.WriteLine("Serial ports (PTT / carrier detect):");
			var ports = SerialPort.GetPortNames();
			if (ports.Length == 0)
				Cli.WriteLine("  (none)");
			foreach (var port in ports)
				Cli.WriteLine($"  {port}");

			Cli.WriteLine("CM108-compatible USB HID interfaces (PTT GPIO):");
			var allHids = Cm108Compatibility.EnumerateHidDevices();
			var hids = Cm108Compatibility.SelectCompatible(allHids);
			if (hids.Count == 0)
				Cli.WriteLine($"  (none found; looked for {string.Join(", ", Cm108Compatibility.KnownInterfaces)})");

			// Which one Cm108 PTT would key with the current settings; an unreadable settings file must not break the listing.
			RadioModeOptions radio = null;
			try { radio = LoadHostOptions().Radio; }
			catch (Exception) { /* list the devices without the configured marker */ }

			var matching = radio == null ? Array.Empty<Resgrid.Audio.Core.Radio.UsbHidDevice>() : Cm108Compatibility.FindAllSelected(allHids, radio.Cm108VendorId, radio.Cm108ProductId);
			var selected = radio == null ? null : Cm108Compatibility.FindSelected(allHids, radio.Cm108VendorId, radio.Cm108ProductId);
			foreach (var hid in hids)
			{
				var note = ReferenceEquals(hid, selected)
					? "  <- selected by the current settings"
					: $"  (to use it: {Cm108Compatibility.SettingsFor(hid)})";
				Cli.WriteLine($"  {Cm108Compatibility.Describe(hid)}{Cm108Compatibility.SameIdsNote(Cm108Compatibility.CountWithSameIds(hids, hid))}{note}");
			}

			if (radio != null)
			{
				var ids = Cm108Compatibility.FormatIds(radio.Cm108VendorId, radio.Cm108ProductId);
				if (matching.Count > 1)
					Cli.WriteLine($"  {Cm108Compatibility.AmbiguousDevicesMessage(radio.Cm108VendorId, radio.Cm108ProductId, matching)}");
				else if (selected == null)
					Cli.WriteLine($"  The current settings ({ids}) select no connected device: Cm108 PTT keying would fail.");
				else if (!hids.Contains(selected))
					Cli.WriteLine($"  The current settings ({ids}) select {Cm108Compatibility.Describe(selected)}, not a known CM108-compatible interface.");
			}

			return 0;
		}

		private static async Task<int> TuneAsync(string[] args)
		{
			var hostOptions = LoadHostOptions();
			if (args.Length > 0 && Int32.TryParse(args[0], out var device))
				hostOptions.Radio.InputDevice = device;

			Cli.WriteLine($"Tuning input device {hostOptions.Radio.InputDevice}. Open={hostOptions.Radio.Squelch.OpenDbfs} dBFS, Close={hostOptions.Radio.Squelch.CloseDbfs} dBFS.");
			Cli.WriteLine("Key up the radio (or feed static) and adjust OpenDbfs just above the static floor. Ctrl+C to stop.");

			// Render the live meter in-place from the engine's tune samples.
			var progress = new Progress<TuneSample>(sample =>
			{
				int bars = Math.Clamp((int)((sample.Dbfs + 80) / 80 * 30), 0, 30);
				var meter = new string('#', bars).PadRight(30, '·');
				Cli.Write($"\r[{meter}] {sample.Dbfs,6:0.0} dBFS  {(sample.SquelchOpen ? "OPEN  " : "closed")}");
			});

			using var cancellationTokenSource = new CancellationTokenSource();
			ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				cancellationTokenSource.Cancel();
			};
			Cli.CancelKeyPress += cancelHandler;
			try
			{
				return await EngineVoice.RadioMode.RunTuneAsync(hostOptions, Logger.None, cancellationTokenSource.Token, progress).ConfigureAwait(false);
			}
			finally
			{
				Cli.CancelKeyPress -= cancelHandler;
				Cli.WriteLine();
			}
		}

		private static async Task<int> TestTransmitAsync()
		{
			var hostOptions = LoadHostOptions();
			var radio = hostOptions.Radio;
			Cli.WriteLine($"Keying the radio ({radio.PttMethod}) on output device {radio.OutputDevice}: {radio.TxLeadMs} ms lead, then a 2-second 1 kHz tone.");
			Cli.WriteLine("Listen on another radio on the same channel/talk group: the whole tone should come through. Ctrl+C cancels.");

			using var cancellationTokenSource = new CancellationTokenSource();
			ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				cancellationTokenSource.Cancel();
			};
			Cli.CancelKeyPress += cancelHandler;
			try
			{
				await EngineVoice.RadioMode.RunTransmitTestAsync(hostOptions, CreateLogger(debug: false), cancellationTokenSource.Token).ConfigureAwait(false);
				Cli.WriteLine("Done. If the start of the tone was cut off, raise Radio:TxLeadMs; if it was distorted, lower Radio:TxGain.");
				return 0;
			}
			catch (OperationCanceledException)
			{
				// The engine throws this only once PTT is confirmed released (or was never keyed); a failed release surfaces as
				// PttReleaseException below instead, so this never claims a release that did not happen.
				Cli.WriteLine("Cancelled; PTT released.");
				return 1;
			}
			catch (Exception ex)
			{
				// Includes PttReleaseException: the radio may still be transmitting, and the message says to disconnect it.
				Cli.Error.WriteLine($"Transmit test failed: {ex.Message}");
				return 1;
			}
			finally
			{
				Cli.CancelKeyPress -= cancelHandler;
			}
		}

		private static async Task<int> MonitorAsync(string[] args)
		{
			var device = 0;
			if (args.Length > 0 && !Int32.TryParse(args[0], out device))
			{
				Cli.Error.WriteLine($"'{args[0]}' is not an audio input device number. Run 'devices' to list them.");
				return 1;
			}
			Logger logger = CreateLogger(debug: true);
			var audioStorage = new WatcherAudioStorage(logger);
			var evaluator = new AudioEvaluator(logger);
			// BeginMonitoring starts the tone detector, which reads its tolerance from the config: without Init it threw a
			// NullReferenceException after the device was already open.
			evaluator.Init(new Config { InputDevice = device });
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

		/// <summary>Like <see cref="Prompt"/>, but never prints the stored value back to the terminal (or its scrollback).</summary>
		private static string PromptSecret(string text, string currentValue)
		{
			Cli.Write($"{text}{(String.IsNullOrWhiteSpace(currentValue) ? String.Empty : " [press Enter to keep the current value]")}: ");
			var input = Cli.ReadLine();
			return String.IsNullOrWhiteSpace(input) ? currentValue : input.Trim();
		}

		private static string Prompt(string text, string defaultValue)
		{
			Cli.Write($"{text}{(String.IsNullOrWhiteSpace(defaultValue) ? String.Empty : $" [{defaultValue}]")}: ");
			var input = Cli.ReadLine();
			return String.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
		}
#else
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

		private static Task<int> TuneAsync(string[] args)
		{
			Cli.Error.WriteLine("Tune is only available in the net10.0-windows build.");
			return Task.FromResult(1);
		}

		private static Task<int> TestTransmitAsync()
		{
			Cli.Error.WriteLine("The transmit test is only available in the net10.0-windows build.");
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
	}
}
