using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Relay.Engine.Configuration;
#if NET10_0_WINDOWS
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Voice;
using Serilog;
#endif

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Editable form over the whole <see cref="RelayHostOptions"/> tree, grouped into sections.
	/// The tree is flattened into <see cref="ObservableValidator"/>-backed fields so required
	/// fields validate via data annotations / INotifyDataErrorInfo.
	///
	/// Secrets (client secret, refresh token, system API key, S3 keys, Sentry DSN, Countly app
	/// key) are loaded as a masked sentinel ("leave unchanged") rather than the real value, and
	/// on save a field still holding the sentinel is skipped so the stored secret is preserved.
	///
	/// The Radio section hosts a live tune meter: Start-tune runs the engine's
	/// <c>RadioMode.RunTuneAsync</c> (Windows only) and reports level/squelch back to the UI via
	/// <see cref="IProgress{T}"/>.
	/// </summary>
	public partial class ConfigurationViewModel : ObservableValidator, IDisposable
	{
		/// <summary>Placeholder shown for a stored secret; if a field still equals this on save the secret is kept.</summary>
		public const string SecretSentinel = "••••••••";

		private readonly ConfigurationService _configuration;
		private bool _disposed;

		public ConfigurationViewModel(ConfigurationService configuration)
		{
			_configuration = configuration;
			LoadFromOptions(_configuration.Current);
		}

		[ObservableProperty]
		private string _title = "Configuration";

		public ConfigurationService Configuration => _configuration;

		/// <summary>True when environment variables override the persisted settings.</summary>
		public bool HasEnvOverrides => _configuration.HasEnvOverrides;

		public string UserConfigPath => _configuration.UserConfigPath;

		[ObservableProperty]
		private string _statusMessage = "";

		// ─────────── General ───────────
		public IReadOnlyList<string> AvailableModes { get; } = new[] { "smtp", "audio", "radio", "record", "dispatch" };

		[ObservableProperty]
		private string _mode = "smtp";

		// ─────────── Resgrid API ───────────
		[ObservableProperty]
		[Required(AllowEmptyStrings = false, ErrorMessage = "Base URL is required.")]
		[NotifyDataErrorInfo]
		private string _resgridBaseUrl = "";

		[ObservableProperty]
		[Required(AllowEmptyStrings = false, ErrorMessage = "Client id is required.")]
		[NotifyDataErrorInfo]
		private string _resgridClientId = "";

		[ObservableProperty]
		private string _resgridClientSecret = "";

		[ObservableProperty]
		private string _resgridRefreshToken = "";

		[ObservableProperty]
		private string _resgridSystemApiKey = "";

		[ObservableProperty]
		private string _resgridDepartmentId = "";

		public IReadOnlyList<ResgridAuthGrantType> GrantTypes { get; } = new[]
		{
			ResgridAuthGrantType.RefreshToken,
			ResgridAuthGrantType.ClientCredentials,
			ResgridAuthGrantType.SystemApiKey
		};

		[ObservableProperty]
		private ResgridAuthGrantType _resgridGrantType = ResgridAuthGrantType.RefreshToken;

		// ─────────── SMTP ───────────
		[ObservableProperty]
		private string _smtpServerName = "";

		[ObservableProperty]
		private int _smtpPort = 2525;

		[ObservableProperty]
		private string _smtpDepartmentAddressDomains = "";

		[ObservableProperty]
		private string _smtpGroupAddressDomains = "";

		[ObservableProperty]
		private bool _smtpHostedMode;

		[ObservableProperty]
		private bool _redisEnabled;

		[ObservableProperty]
		private string _redisConnectionString = "";

		[ObservableProperty]
		private int _redisTtlMinutes = 60;

		// ─────────── Voice ───────────
		[ObservableProperty]
		private string _voiceChannel = "default";

		[ObservableProperty]
		private string _voiceDepartmentId = "";

		[ObservableProperty]
		private bool _voiceEnforceSeatLimit = true;

		// ─────────── Radio ───────────
		[ObservableProperty]
		private int _radioInputDevice;

		[ObservableProperty]
		private int _radioOutputDevice = -1;

		[ObservableProperty]
		private string _radioPttMethod = "Vox";

		[ObservableProperty]
		private string _radioSerialPort = "";

		[ObservableProperty]
		private double _radioTxGain = 1.0;

		[ObservableProperty]
		private double _radioRxGain = 1.0;

		// Squelch
		[ObservableProperty]
		private string _squelchMode = "Vox";

		[ObservableProperty]
		private double _squelchOpenDbfs = -38;

		[ObservableProperty]
		private double _squelchCloseDbfs = -45;

		[ObservableProperty]
		private int _squelchHangMs = 600;

		// Emergency
		[ObservableProperty]
		private bool _emergencyDetectMdc1200;

		[ObservableProperty]
		private bool _emergencyDetectTones;

		[ObservableProperty]
		private bool _emergencyCreateCall;

		// ─────────── Recorder ───────────
		[ObservableProperty]
		private string _recorderChannel = "all";

		[ObservableProperty]
		private string _recorderStore = "local";

		[ObservableProperty]
		private string _recorderLocalPath = "";

		// S3
		[ObservableProperty]
		private string _s3Endpoint = "";

		[ObservableProperty]
		private string _s3Bucket = "";

		[ObservableProperty]
		private string _s3Region = "";

		[ObservableProperty]
		private string _s3AccessKey = "";

		[ObservableProperty]
		private string _s3SecretKey = "";

		// ─────────── Dispatch voice ───────────
		[ObservableProperty]
		private string _dispatchChannel = "default";

		[ObservableProperty]
		private int _dispatchPollSeconds = 15;

		// ─────────── TTS ───────────
		[ObservableProperty]
		private string _ttsServiceBaseUrl = "";

		[ObservableProperty]
		private string _ttsVoice = "";

		[ObservableProperty]
		private int _ttsSpeed;

		// ─────────── Telemetry ───────────
		[ObservableProperty]
		private string _telemetryEnvironment = "";

		[ObservableProperty]
		private string _sentryDsn = "";

		[ObservableProperty]
		private string _countlyUrl = "";

		[ObservableProperty]
		private string _countlyAppKey = "";

		// ─────────── Secret reveal toggle ───────────
		[ObservableProperty]
		private bool _revealSecrets;

		// ─────────── Tune meter ───────────
		[ObservableProperty]
		private bool _isTuning;

		[ObservableProperty]
		private double _tuneDbfs = -80;

		[ObservableProperty]
		private bool _tuneSquelchOpen;

		[ObservableProperty]
		private string _tuneStatus = "";

		/// <summary>Tune is only available on Windows (NAudio capture + the engine RadioMode tuner,
		/// both of which compile only under the NET10_0_WINDOWS symbol).</summary>
		public bool CanTune => OperatingSystem.IsWindows();

#if NET10_0_WINDOWS
		private CancellationTokenSource _tuneCts;
#endif

		// ─────────── Load / Save ───────────

		private void LoadFromOptions(RelayHostOptions o)
		{
			Mode = o.Mode ?? "smtp";

			var api = o.Resgrid ?? new ResgridApiClientOptions();
			ResgridBaseUrl = api.BaseUrl ?? "";
			ResgridClientId = api.ClientId ?? "";
			ResgridClientSecret = MaskIfPresent(api.ClientSecret);
			ResgridRefreshToken = MaskIfPresent(api.RefreshToken);
			ResgridSystemApiKey = MaskIfPresent(api.SystemApiKey);
			ResgridDepartmentId = api.DepartmentId ?? "";
			ResgridGrantType = api.GrantType;

			var smtp = o.Smtp;
			if (smtp != null)
			{
				SmtpServerName = smtp.ServerName ?? "";
				SmtpPort = smtp.Port;
				SmtpDepartmentAddressDomains = JoinDomains(smtp.DepartmentAddressDomains);
				SmtpGroupAddressDomains = JoinDomains(smtp.GroupAddressDomains);
				SmtpHostedMode = smtp.HostedMode;

				var redis = smtp.RedisCache;
				if (redis != null)
				{
					RedisEnabled = redis.Enabled;
					RedisConnectionString = redis.ConnectionString ?? "";
					RedisTtlMinutes = redis.TtlMinutes;
				}
			}

			var voice = o.Voice;
			if (voice != null)
			{
				VoiceChannel = voice.Channel ?? "default";
				VoiceDepartmentId = voice.DepartmentId ?? "";
				VoiceEnforceSeatLimit = voice.EnforceSeatLimit;
			}

			var radio = o.Radio;
			if (radio != null)
			{
				RadioInputDevice = radio.InputDevice;
				RadioOutputDevice = radio.OutputDevice;
				RadioPttMethod = radio.PttMethod ?? "Vox";
				RadioSerialPort = radio.SerialPort ?? "";
				RadioTxGain = radio.TxGain;
				RadioRxGain = radio.RxGain;

				var sq = radio.Squelch;
				if (sq != null)
				{
					SquelchMode = sq.Mode.ToString();
					SquelchOpenDbfs = sq.OpenDbfs;
					SquelchCloseDbfs = sq.CloseDbfs;
					SquelchHangMs = sq.HangMs;
				}

				var em = radio.Emergency;
				if (em != null)
				{
					EmergencyDetectMdc1200 = em.DetectMdc1200;
					EmergencyDetectTones = em.DetectTones;
					EmergencyCreateCall = em.CreateCall;
				}
			}

			var rec = o.Recorder;
			if (rec != null)
			{
				RecorderChannel = rec.Channel ?? "all";
				RecorderStore = rec.Store ?? "local";
				RecorderLocalPath = rec.LocalPath ?? "";

				var s3 = rec.S3;
				if (s3 != null)
				{
					S3Endpoint = s3.Endpoint ?? "";
					S3Bucket = s3.Bucket ?? "";
					S3Region = s3.Region ?? "";
					S3AccessKey = MaskIfPresent(s3.AccessKey);
					S3SecretKey = MaskIfPresent(s3.SecretKey);
				}
			}

			var dv = o.DispatchVoice;
			if (dv != null)
			{
				DispatchChannel = dv.Channel ?? "default";
				DispatchPollSeconds = dv.PollSeconds;
			}

			var tts = o.Tts;
			if (tts != null)
			{
				TtsServiceBaseUrl = tts.ServiceBaseUrl ?? "";
				TtsVoice = tts.Voice ?? "";
				TtsSpeed = tts.Speed;
			}

			var tel = o.Telemetry;
			if (tel != null)
			{
				TelemetryEnvironment = tel.Environment ?? "";
				SentryDsn = MaskIfPresent(tel.Sentry?.Dsn);
				CountlyUrl = tel.Countly?.Url ?? "";
				CountlyAppKey = MaskIfPresent(tel.Countly?.AppKey);
			}

			ValidateAllProperties();
		}

		/// <summary>Writes the form back into <paramref name="o"/>, preserving sentinel-held secrets.</summary>
		private void ApplyToOptions(RelayHostOptions o)
		{
			o.Mode = Mode;

			o.Resgrid ??= new ResgridApiClientOptions();
			o.Resgrid.BaseUrl = ResgridBaseUrl;
			o.Resgrid.ClientId = ResgridClientId;
			o.Resgrid.ClientSecret = MergeSecret(ResgridClientSecret, o.Resgrid.ClientSecret);
			o.Resgrid.RefreshToken = MergeSecret(ResgridRefreshToken, o.Resgrid.RefreshToken);
			o.Resgrid.SystemApiKey = MergeSecret(ResgridSystemApiKey, o.Resgrid.SystemApiKey);
			o.Resgrid.DepartmentId = ResgridDepartmentId;
			o.Resgrid.GrantType = ResgridGrantType;

			o.Smtp ??= new SmtpRelayOptions();
			o.Smtp.ServerName = SmtpServerName;
			o.Smtp.Port = SmtpPort;
			o.Smtp.DepartmentAddressDomains = SplitDomains(SmtpDepartmentAddressDomains);
			o.Smtp.GroupAddressDomains = SplitDomains(SmtpGroupAddressDomains);
			o.Smtp.HostedMode = SmtpHostedMode;
			o.Smtp.RedisCache ??= new RedisCacheOptions();
			o.Smtp.RedisCache.Enabled = RedisEnabled;
			o.Smtp.RedisCache.ConnectionString = RedisConnectionString;
			o.Smtp.RedisCache.TtlMinutes = RedisTtlMinutes;

			o.Voice ??= new VoiceConnectionOptions();
			o.Voice.Channel = VoiceChannel;
			o.Voice.DepartmentId = VoiceDepartmentId;
			o.Voice.EnforceSeatLimit = VoiceEnforceSeatLimit;

			o.Radio ??= new RadioModeOptions();
			o.Radio.InputDevice = RadioInputDevice;
			o.Radio.OutputDevice = RadioOutputDevice;
			o.Radio.PttMethod = RadioPttMethod;
			o.Radio.SerialPort = RadioSerialPort;
			o.Radio.TxGain = RadioTxGain;
			o.Radio.RxGain = RadioRxGain;
			o.Radio.Squelch ??= new Resgrid.Audio.Voice.Dsp.SquelchSettings();
			if (Enum.TryParse<Resgrid.Audio.Voice.Dsp.SquelchMode>(SquelchMode, out var sqMode))
				o.Radio.Squelch.Mode = sqMode;
			o.Radio.Squelch.OpenDbfs = SquelchOpenDbfs;
			o.Radio.Squelch.CloseDbfs = SquelchCloseDbfs;
			o.Radio.Squelch.HangMs = SquelchHangMs;
			o.Radio.Emergency ??= new EmergencyOptions();
			o.Radio.Emergency.DetectMdc1200 = EmergencyDetectMdc1200;
			o.Radio.Emergency.DetectTones = EmergencyDetectTones;
			o.Radio.Emergency.CreateCall = EmergencyCreateCall;

			o.Recorder ??= new RecorderModeOptions();
			o.Recorder.Channel = RecorderChannel;
			o.Recorder.Store = RecorderStore;
			o.Recorder.LocalPath = RecorderLocalPath;
			o.Recorder.S3 ??= new S3StorageOptions();
			o.Recorder.S3.Endpoint = S3Endpoint;
			o.Recorder.S3.Bucket = S3Bucket;
			o.Recorder.S3.Region = S3Region;
			o.Recorder.S3.AccessKey = MergeSecret(S3AccessKey, o.Recorder.S3.AccessKey);
			o.Recorder.S3.SecretKey = MergeSecret(S3SecretKey, o.Recorder.S3.SecretKey);

			o.DispatchVoice ??= new DispatchVoiceOptions();
			o.DispatchVoice.Channel = DispatchChannel;
			o.DispatchVoice.PollSeconds = DispatchPollSeconds;

			o.Tts ??= new Resgrid.Audio.Voice.ToneOut.TtsSettings();
			o.Tts.ServiceBaseUrl = TtsServiceBaseUrl;
			o.Tts.Voice = TtsVoice;
			o.Tts.Speed = TtsSpeed;

			o.Telemetry ??= new RelayTelemetryOptions();
			o.Telemetry.Environment = TelemetryEnvironment;
			o.Telemetry.Sentry ??= new SentryTelemetryOptions();
			o.Telemetry.Sentry.Dsn = MergeSecret(SentryDsn, o.Telemetry.Sentry.Dsn);
			o.Telemetry.Countly ??= new CountlyTelemetryOptions();
			o.Telemetry.Countly.Url = CountlyUrl;
			o.Telemetry.Countly.AppKey = MergeSecret(CountlyAppKey, o.Telemetry.Countly.AppKey);
		}

		[RelayCommand]
		private void Save()
		{
			ValidateAllProperties();
			if (HasErrors)
			{
				StatusMessage = "Cannot save — fix the highlighted required fields.";
				return;
			}

			// Start from a disk-only snapshot (no RESGRID__RELAY__ env overrides) so unedited/
			// unknown fields survive without baking environment values into the user config file.
			var options = _configuration.LoadFromDisk();
			ApplyToOptions(options);

			// Validate the full Resgrid API contract (client secret + the grant-type-specific
			// token/key requirements), not just the annotated fields, before persisting.
			try
			{
				options.Resgrid.Validate();
			}
			catch (InvalidOperationException ex)
			{
				StatusMessage = $"Cannot save — {ex.Message}";
				return;
			}

			_configuration.Save(options);

			// Re-load so masked secrets reflect the now-stored values.
			LoadFromOptions(_configuration.Current);
			StatusMessage = $"Saved to {_configuration.UserConfigPath}";
		}

		[RelayCommand]
		private void Reload()
		{
			LoadFromOptions(_configuration.Reload());
			OnPropertyChanged(nameof(HasEnvOverrides));
			StatusMessage = "Reloaded from disk.";
		}

		// ─────────── Tune meter ───────────

		// AllowConcurrentExecutions so the command (and its bound button) stays enabled while the
		// long-running tune meter is active — otherwise CanExecute would be false for the whole
		// session and the IsTuning→StopTune branch would be unreachable (can't stop without leaving).
		[RelayCommand(AllowConcurrentExecutions = true)]
		private async Task ToggleTune()
		{
			if (IsTuning)
			{
				StopTune();
				return;
			}

			await StartTuneAsync().ConfigureAwait(true);
		}

		private async Task StartTuneAsync()
		{
#if NET10_0_WINDOWS
			if (!OperatingSystem.IsWindows())
			{
				TuneStatus = "Tuning is only available on Windows.";
				return;
			}

			// Tune against an isolated snapshot with the edited form values overlaid, so uncommitted
			// form changes never mutate the shared Current that active services (RelayController) read.
			// RunTuneAsync only consumes the Radio settings; the rest of the clone is throwaway.
			var options = _configuration.LoadFromDisk();
			ApplyToOptions(options);

			_tuneCts?.Dispose();
			_tuneCts = new CancellationTokenSource();
			IsTuning = true;
			TuneStatus = "Tuning… key the radio to see levels.";

			var progress = new Progress<TuneSample>(sample =>
			{
				TuneDbfs = sample.Dbfs;
				TuneSquelchOpen = sample.SquelchOpen;
			});

			try
			{
				await RadioMode.RunTuneAsync(options, Log.Logger, _tuneCts.Token, progress).ConfigureAwait(true);
			}
			catch (OperationCanceledException)
			{
				// Normal on Stop.
			}
			catch (Exception ex)
			{
				TuneStatus = $"Tune failed: {ex.Message}";
			}
			finally
			{
				IsTuning = false;
				// Dispose this session's CTS so repeated tune runs don't leak; null so a
				// subsequent StopTune/Start sees no stale source.
				_tuneCts?.Dispose();
				_tuneCts = null;
			}
#else
			TuneStatus = "Tuning is only available on Windows.";
			await Task.CompletedTask;
#endif
		}

		private void StopTune()
		{
#if NET10_0_WINDOWS
			try { _tuneCts?.Cancel(); } catch { /* already disposed */ }
#endif
			IsTuning = false;
			if (!string.IsNullOrEmpty(TuneStatus))
				TuneStatus = "Tuning stopped.";
		}

		// ─────────── Secret helpers ───────────

		private static string MaskIfPresent(string stored)
			=> string.IsNullOrEmpty(stored) ? "" : SecretSentinel;

		/// <summary>
		/// Resolves an edited secret field against the stored value: an unchanged sentinel keeps
		/// the stored secret; otherwise the typed value (including a deliberate blank) wins.
		/// </summary>
		private static string MergeSecret(string edited, string stored)
			=> edited == SecretSentinel ? stored : edited;

		private static string JoinDomains(string[] domains)
			=> domains == null ? "" : string.Join(", ", domains);

		private static string[] SplitDomains(string csv)
		{
			if (string.IsNullOrWhiteSpace(csv))
				return Array.Empty<string>();

			var parts = csv.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
			var list = new List<string>(parts.Length);
			foreach (var p in parts)
			{
				var trimmed = p.Trim();
				if (trimmed.Length > 0)
					list.Add(trimmed);
			}
			return list.ToArray();
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			StopTune();
#if NET10_0_WINDOWS
			_tuneCts?.Dispose();
#endif
		}
	}
}
