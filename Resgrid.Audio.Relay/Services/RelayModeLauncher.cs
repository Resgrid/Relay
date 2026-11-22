using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Serilog;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>
	/// Starts, stops and pre-flight-checks relay modes for every part of the desktop UI (Operations cards, the shell's
	/// Start/Stop buttons, the tray menu and auto-start at launch), so they all apply the same rules. Each start runs on a
	/// deep copy of the current settings with <c>Mode</c> forced, so the shared settings object is never mutated.
	/// </summary>
	public sealed class RelayModeLauncher
	{
		public static readonly IReadOnlyList<string> AllModes = new[] { "smtp", "audio", "radio", "record", "dispatch" };

		/// <summary>How long a start waits for a stop, another start or a radio claim on the same mode before giving up.</summary>
		private static readonly TimeSpan LifecycleWaitTimeout = TimeSpan.FromSeconds(30);

		private readonly RelayController _controller;
		private readonly ConfigurationService _configuration;
		private readonly DesktopSettingsService _desktop;
		private readonly ILogger _logger;

		// One gate per mode, shared by its start and stop paths: a restart of a faulted instance awaits its teardown, and a
		// second start that ran in that window found nothing tracked and started its own copy, leaving two of the same mode
		// running. Modes don't share a gate, so unrelated modes still start independently.
		private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates =
			new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

		// Set while a diagnostic (the Configuration screen's transmit test) holds the "radio" gate through TryClaimRadio.
		private volatile bool _radioClaimed;

		public RelayModeLauncher(RelayController controller, ConfigurationService configuration, DesktopSettingsService desktop, ILogger logger)
		{
			_controller = controller;
			_configuration = configuration;
			_desktop = desktop;
			_logger = logger;
		}

		private SemaphoreSlim Gate(string mode) => _lifecycleGates.GetOrAdd(mode ?? "", _ => new SemaphoreSlim(1, 1));

		/// <summary>
		/// Claims the radio's audio devices and PTT line for a diagnostic outside the Radio Bridge (the transmit test), so the
		/// bridge cannot start while the diagnostic holds or is still releasing them — a bridge start waits for the claim —
		/// and the diagnostic cannot start while the bridge runs, starts or stops. Returns null, with the reason, when the radio
		/// is busy. Dispose the claim only once the diagnostic has finished its cleanup (PTT released, devices closed).
		/// </summary>
		public IDisposable TryClaimRadio(out string reason)
		{
			var gate = Gate("radio");
			if (!gate.Wait(0))
			{
				reason = _radioClaimed
					? "The radio is already in use by another test."
					: "The Radio Bridge is starting or stopping; try again in a moment.";
				return null;
			}

			if (IsActive("radio"))
			{
				gate.Release();
				reason = "Stop the Radio Bridge first — it already owns the radio's audio and PTT.";
				return null;
			}

			_radioClaimed = true;
			reason = null;
			return new RadioClaim(this, gate);
		}

		private sealed class RadioClaim : IDisposable
		{
			private RelayModeLauncher _owner;
			private readonly SemaphoreSlim _gate;

			public RadioClaim(RelayModeLauncher owner, SemaphoreSlim gate)
			{
				_owner = owner;
				_gate = gate;
			}

			public void Dispose()
			{
				var owner = Interlocked.Exchange(ref _owner, null);
				if (owner == null)
					return;
				owner._radioClaimed = false;
				_gate.Release();
			}
		}

		public RelayController Controller => _controller;

		public static string FriendlyName(string mode)
		{
			switch (mode)
			{
				case "smtp": return "SMTP Dispatch";
				case "audio": return "Audio Tone-Detect";
				case "radio": return "Radio Bridge";
				case "record": return "Recorder";
				case "dispatch": return "Dispatch Tone-Out";
				default:
					return string.IsNullOrEmpty(mode) ? "Service" : char.ToUpperInvariant(mode[0]) + mode.Substring(1);
			}
		}

		/// <summary>True when a service for <paramref name="mode"/> is tracked and not faulted/stopped.</summary>
		public bool IsActive(string mode)
		{
			var service = _controller.Find(mode);
			return service != null && service.State != RelayServiceState.Faulted && service.State != RelayServiceState.Stopped;
		}

		/// <summary>
		/// Pre-flight problems for <paramref name="mode"/> against the current settings; empty means ready to start. Mirrors the
		/// checks the engine would otherwise fail on at start (credentials, grant type, devices, required paths).
		/// </summary>
		public IReadOnlyList<string> Validate(string mode)
		{
			var o = _configuration.Current;
			var errors = new List<string>();
			if (!string.IsNullOrEmpty(_configuration.LoadError))
				errors.Add("Settings could not be read: " + _configuration.LoadError);

			// Audio mode may take its credentials from its watcher file (written by the console 'setup'); the engine merges both
			// and validates the result, so only the other modes are checked here.
			if (mode != "audio")
			{
				try
				{
					(o.Resgrid ?? new ResgridApiClientOptions()).Validate();
				}
				catch (InvalidOperationException ex)
				{
					errors.Add(ex.Message);
				}
			}

			var grant = o.Resgrid?.GrantType ?? ResgridAuthGrantType.RefreshToken;
			bool isVoiceMode = mode == "radio" || mode == "record" || mode == "dispatch";
			if (isVoiceMode && grant != ResgridAuthGrantType.RefreshToken)
				errors.Add("Voice modes need the RefreshToken grant (a user's refresh token); Resgrid's Voice API refuses " + grant + ".");

			switch (mode)
			{
				case "smtp":
				{
					var smtp = o.Smtp ?? new SmtpRelayOptions();
					var hasDomains = (smtp.DepartmentAddressDomains?.Length ?? 0) > 0
						|| (smtp.GroupAddressDomains?.Length ?? 0) > 0
						|| (smtp.GroupMessageAddressDomains?.Length ?? 0) > 0
						|| (smtp.ListAddressDomains?.Length ?? 0) > 0;
					if (!hasDomains)
						errors.Add("At least one SMTP dispatch domain must be configured.");
					if (smtp.Port < 1 || smtp.Port > 65535)
						errors.Add("The SMTP port must be between 1 and 65535.");
					if (smtp.RedisCache is { Enabled: true } && string.IsNullOrWhiteSpace(smtp.RedisCache.ConnectionString))
						errors.Add("Redis cache is enabled but the connection string is empty.");
					break;
				}

				case "audio":
				{
					if (string.IsNullOrWhiteSpace(o.AudioConfigPath) || !File.Exists(o.AudioConfigPath))
						errors.Add($"The tone-detect watcher file was not found ({o.AudioConfigPath}). Create it with the console 'setup' command.");
					break;
				}

				case "radio":
				{
					var radio = o.Radio ?? new RadioModeOptions();
					int inputs = SafeCount(() => WaveIn.DeviceCount);
					int outputs = SafeCount(() => WaveOut.DeviceCount);
					if (inputs == 0)
						errors.Add("No audio input device was found for radio receive audio.");
					else if (radio.InputDevice < 0 || radio.InputDevice >= inputs)
						errors.Add($"Radio input device {radio.InputDevice} does not exist (0–{inputs - 1} available).");
					if (radio.OutputDevice < -1 || radio.OutputDevice >= outputs)
						errors.Add($"Radio output device {radio.OutputDevice} does not exist (-1 = default, 0–{Math.Max(0, outputs - 1)} available).");

					var ptt = radio.PttMethod ?? "Vox";
					var carrier = radio.CarrierDetect ?? "None";
					bool serialPtt = ptt.Equals("SerialRts", StringComparison.OrdinalIgnoreCase) || ptt.Equals("SerialDtr", StringComparison.OrdinalIgnoreCase);
					bool serialCarrier = carrier.Equals("SerialCts", StringComparison.OrdinalIgnoreCase) || carrier.Equals("SerialDsr", StringComparison.OrdinalIgnoreCase);
					if ((serialPtt || serialCarrier) && string.IsNullOrWhiteSpace(radio.SerialPort))
						errors.Add("Serial PTT or carrier detect is selected but no serial port is set.");

					var profile = RadioProfiles.Find(radio.Profile);
					if (profile.IsPushToTalkOverCellular && ptt.Equals("Vox", StringComparison.OrdinalIgnoreCase))
						errors.Add($"{profile.DisplayName} needs a hard PTT line (Cm108, SerialRts or SerialDtr), not Vox.");
					break;
				}

				case "record":
				{
					var store = (o.Recorder?.Store ?? "local").Trim().ToLowerInvariant();
					if ((store == "s3" || store == "both") && string.IsNullOrWhiteSpace(o.Recorder?.S3?.Bucket))
						errors.Add("Recorder S3 storage is selected but no S3 bucket is configured.");
					break;
				}

				case "dispatch":
				{
					if (string.IsNullOrWhiteSpace(o.Tts?.ServiceBaseUrl))
						errors.Add("Dispatch tone-out needs a TTS service URL.");
					break;
				}
			}

			return errors;
		}

		private static int SafeCount(Func<int> count)
		{
			try { return count(); }
			catch { return 0; }
		}

		/// <summary>
		/// Starts <paramref name="mode"/>. A faulted instance of the same mode is cleared first. Returns null on success, or the
		/// reason it was not started. Serialized with every other start and stop of the same mode; a Radio Bridge start also
		/// waits for a transmit test to give the radio back (<see cref="TryClaimRadio"/>).
		/// </summary>
		public async Task<string> StartAsync(string mode)
		{
			var gate = Gate(mode);
			if (!await gate.WaitAsync(LifecycleWaitTimeout).ConfigureAwait(true))
			{
				return string.Equals(mode, "radio", StringComparison.OrdinalIgnoreCase) && _radioClaimed
					? $"{FriendlyName(mode)} not started: the radio transmit test (Configuration) is still using the radio. Stop it and try again."
					: $"{FriendlyName(mode)} not started: it is still starting or stopping. Try again in a moment.";
			}

			try
			{
				// Checked inside the gate, after any teardown: nothing else can start this mode meanwhile.
				var existing = _controller.Find(mode);
				if (existing != null)
				{
					if (existing.State != RelayServiceState.Faulted && existing.State != RelayServiceState.Stopped)
						return $"{FriendlyName(mode)} is already running.";
					await _controller.StopAsync(existing).ConfigureAwait(true);
				}

				var errors = Validate(mode);
				if (errors.Count > 0)
					return $"{FriendlyName(mode)} not started: {errors[0]}";

				try
				{
					var options = CloneOptions(_configuration.Current);
					options.Mode = mode;
					_controller.Start(options);
					_logger?.Information("Started {Mode}", FriendlyName(mode));
					return null;
				}
				catch (Exception ex)
				{
					_logger?.Error(ex, "Could not start {Mode}", FriendlyName(mode));
					return $"{FriendlyName(mode)} not started: {ex.Message}";
				}
			}
			finally
			{
				gate.Release();
			}
		}

		public async Task StopAsync(string mode)
		{
			var gate = Gate(mode);
			await gate.WaitAsync().ConfigureAwait(true);
			try
			{
				var service = _controller.Find(mode);
				if (service != null)
					await _controller.StopAsync(service).ConfigureAwait(true);
			}
			finally
			{
				gate.Release();
			}
		}

		public async Task<string> RestartAsync(string mode)
		{
			// A stop-all (footer, tray, quit) issued while the old instance is being torn down must not be reversed by the start.
			var stopAllRequests = _controller.StopAllRequests;
			await StopAsync(mode).ConfigureAwait(true);
			if (_controller.StopAllRequests != stopAllRequests)
				return $"{FriendlyName(mode)} stopped; restart cancelled.";
			return await StartAsync(mode).ConfigureAwait(true);
		}

		/// <summary>The modes the master Start runs: the auto-start set, or the configured default mode when none is chosen.</summary>
		public IReadOnlyList<string> DefaultModes()
		{
			var auto = _desktop.Current.AutoStartModes
				.Where(m => AllModes.Contains(m, StringComparer.OrdinalIgnoreCase))
				.Select(m => m.ToLowerInvariant())
				.Distinct()
				.ToList();
			if (auto.Count > 0)
				return auto;

			var configured = (_configuration.Current.Mode ?? "smtp").Trim().ToLowerInvariant();
			return AllModes.Contains(configured) ? new[] { configured } : new[] { "smtp" };
		}

		/// <summary>Starts every default mode that isn't already running; returns the problems (empty when all started).</summary>
		public async Task<IReadOnlyList<string>> StartDefaultModesAsync()
		{
			var problems = new List<string>();
			foreach (var mode in DefaultModes())
			{
				if (IsActive(mode))
					continue;
				var error = await StartAsync(mode).ConfigureAwait(true);
				if (error != null)
					problems.Add(error);
			}
			return problems;
		}

		/// <summary>Starts the modes chosen for auto-start at launch; returns the problems.</summary>
		public async Task<IReadOnlyList<string>> StartAutoStartModesAsync()
		{
			var problems = new List<string>();
			foreach (var mode in AllModes.Where(_desktop.IsAutoStart))
			{
				var error = await StartAsync(mode).ConfigureAwait(true);
				if (error != null)
					problems.Add(error);
			}
			return problems;
		}

		private static RelayHostOptions CloneOptions(RelayHostOptions source)
		{
			var json = JsonSerializer.Serialize(source);
			return JsonSerializer.Deserialize<RelayHostOptions>(json) ?? new RelayHostOptions();
		}
	}
}
