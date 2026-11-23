using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Relay.Engine.Configuration;
using Cm108Compatibility = Resgrid.Audio.Core.Radio.Cm108Compatibility;
#if NET10_0_WINDOWS
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Voice;
using Serilog;
#endif

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Editable form over the whole <see cref="RelayHostOptions"/> tree, grouped into sections. The tree is flattened into
	/// <see cref="ObservableValidator"/>-backed fields so required fields validate via data annotations.
	///
	/// Secrets (client secret, refresh token, system API key, S3 keys, Sentry DSN, Countly app key) are loaded as a masked
	/// sentinel ("leave unchanged") rather than the real value, and on save a field still holding the sentinel keeps the
	/// stored secret.
	///
	/// The Radio section picks devices by name, applies donor-radio profiles (analog, Rapid Radios, Diga-Talk+, other LTE),
	/// hosts the live receive tune meter and a transmit test that keys the radio and plays a tone after the key-up lead.
	/// Tune, transmit test and the connection test all run against the edited form values without saving them.
	/// </summary>
	public partial class ConfigurationViewModel : ObservableValidator, IDisposable
	{
		/// <summary>Placeholder shown for a stored secret; if a field still equals this on save the secret is kept.</summary>
		public const string SecretSentinel = "••••••••";

		private readonly ConfigurationService _configuration;
		private readonly DeviceEnumerationService _devices;
		private readonly RelayModeLauncher _launcher;
		private bool _disposed;
		private bool _loading;

		// Why the on-disk layers (appsettings.json + relay.user.json) could not be read when the form was last populated from
		// them, or null. Tracked apart from the effective load error: an environment value can mask a bad value in the file,
		// and the form then shows defaults that a Save would write over the file's other settings. Only repopulating the form
		// (open, Reload, after a Save) updates it: a file repaired meanwhile must not let a Save write those defaults over it.
		private string _diskLoadError;

		public ConfigurationViewModel(ConfigurationService configuration, DeviceEnumerationService devices, RelayModeLauncher launcher)
		{
			_configuration = configuration;
			_devices = devices;
			_launcher = launcher;
			RefreshDeviceLists();
			// The form edits the on-disk layers only, so a Save never writes RESGRID__RELAY__ environment values into the
			// user file (the banner says when the environment overrides what is shown).
			LoadFormFromDisk();
		}

		/// <summary>Why the settings files could not be read (defaults are shown), or null.</summary>
		public string LoadError => _diskLoadError ?? _configuration.LoadError;

		public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

		/// <summary>The settings file path with the user's profile folder abbreviated.</summary>
		public string UserConfigPathDisplay => App.DisplayPath(_configuration.UserConfigPath);

		[ObservableProperty]
		private string _title = "Configuration";

		public ConfigurationService Configuration => _configuration;

		/// <summary>True when environment variables override the persisted settings.</summary>
		public bool HasEnvOverrides => _configuration.HasEnvOverrides;

		public string UserConfigPath => _configuration.UserConfigPath;

		[ObservableProperty]
		private string _statusMessage = "";

		[ObservableProperty]
		private bool _isDirty;

		// ─────────── Option lists ───────────
		public IReadOnlyList<string> AvailableModes { get; } = RelayModeLauncher.AllModes;

		public IReadOnlyList<ResgridAuthGrantType> GrantTypes { get; } = new[]
		{
			ResgridAuthGrantType.RefreshToken,
			ResgridAuthGrantType.ClientCredentials,
			ResgridAuthGrantType.SystemApiKey
		};

		public IReadOnlyList<string> PttMethods { get; } = new[] { "Cm108", "SerialRts", "SerialDtr", "Vox" };
		public IReadOnlyList<string> CarrierDetectSources { get; } = new[] { "None", "SerialCts", "SerialDsr" };
		public IReadOnlyList<string> SquelchModes { get; } = new[] { "Vox", "Carrier", "Ctcss", "Off" };
		public IReadOnlyList<string> RecorderStores { get; } = new[] { "local", "s3", "both" };
		public IReadOnlyList<string> RecorderLogs { get; } = new[] { "jsonl", "sqlite", "none" };
		public IReadOnlyList<RadioProfile> RadioProfileOptions { get; } = RadioProfiles.All;

		public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new ObservableCollection<AudioDeviceInfo>();
		public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new ObservableCollection<AudioDeviceInfo>();
		public ObservableCollection<string> SerialPorts { get; } = new ObservableCollection<string>();
		public ObservableCollection<HidDeviceInfo> Cm108Devices { get; } = new ObservableCollection<HidDeviceInfo>();

		// ─────────── General ───────────
		[ObservableProperty]
		private string _mode = "smtp";

		// ─────────── Resgrid API ───────────
		// Required unless the RESGRID__RELAY__ environment supplies it: the form shows the file's (blank) value then, and a
		// plain [Required] blocked every Save. Save still validates the effective options, so a value missing from both fails.
		[ObservableProperty]
		[CustomValidation(typeof(ConfigurationViewModel), nameof(ValidateResgridBaseUrl))]
		[NotifyDataErrorInfo]
		private string _resgridBaseUrl = "";

		[ObservableProperty]
		[CustomValidation(typeof(ConfigurationViewModel), nameof(ValidateResgridClientId))]
		[NotifyDataErrorInfo]
		private string _resgridClientId = "";

		public static ValidationResult ValidateResgridBaseUrl(string value, ValidationContext context)
			=> RequiredUnlessFromEnvironment(value, "Resgrid:BaseUrl", "Base URL is required.", context);

		public static ValidationResult ValidateResgridClientId(string value, ValidationContext context)
			=> RequiredUnlessFromEnvironment(value, "Resgrid:ClientId", "Client id is required.", context);

		private static ValidationResult RequiredUnlessFromEnvironment(string value, string key, string error, ValidationContext context)
		{
			if (!string.IsNullOrWhiteSpace(value) || !string.IsNullOrWhiteSpace(RelayConfiguration.EnvironmentValue(key)))
				return ValidationResult.Success;
			return new ValidationResult(error, context?.MemberName == null ? null : new[] { context.MemberName });
		}

		[ObservableProperty]
		private string _resgridClientSecret = "";

		[ObservableProperty]
		private string _resgridRefreshToken = "";

		[ObservableProperty]
		private string _resgridSystemApiKey = "";

		[ObservableProperty]
		private string _resgridDepartmentId = "";

		[ObservableProperty]
		private ResgridAuthGrantType _resgridGrantType = ResgridAuthGrantType.RefreshToken;

		[ObservableProperty]
		private bool _resgridServiceAccount;

		[ObservableProperty]
		[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
		private bool _isTestingConnection;

		[ObservableProperty]
		private string _connectionTestStatus = "";

		// ─────────── SMTP ───────────
		[ObservableProperty] private string _smtpServerName = "";
		[ObservableProperty] private int _smtpPort = 2525;
		[ObservableProperty] private string _smtpDepartmentAddressDomains = "";
		[ObservableProperty] private string _smtpGroupAddressDomains = "";
		[ObservableProperty] private string _smtpGroupMessageAddressDomains = "";
		[ObservableProperty] private string _smtpListAddressDomains = "";
		[ObservableProperty] private string _smtpDataDirectory = "";
		[ObservableProperty] private string _smtpDefaultDepartmentId = "";
		[ObservableProperty] private bool _smtpResolveDispatchCodes = true;
		[ObservableProperty] private bool _smtpValidateDepartmentCodes = true;
		[ObservableProperty] private int _smtpDefaultCallPriority = 1;
		[ObservableProperty] private string _smtpDefaultCallType = "";
		[ObservableProperty] private int _smtpDuplicateWindowHours = 72;
		[ObservableProperty] private bool _smtpSaveRawMessages = true;
		[ObservableProperty] private bool _smtpHostedMode;
		[ObservableProperty] private bool _redisEnabled;
		[ObservableProperty] private string _redisConnectionString = "";
		[ObservableProperty] private int _redisTtlMinutes = 60;

		// ─────────── Audio tone-detect ───────────
		[ObservableProperty] private string _audioConfigPath = "";

		// ─────────── Voice ───────────
		[ObservableProperty] private string _voiceChannel = "default";
		[ObservableProperty] private string _voiceDepartmentId = "";
		[ObservableProperty] private bool _voiceEnforceSeatLimit = true;

		// ─────────── Radio ───────────
		[ObservableProperty] private RadioProfile _selectedRadioProfile = RadioProfiles.Custom;
		[ObservableProperty] private string _radioProfileKey = RadioProfiles.CustomKey;
		[ObservableProperty] private int _radioInputDevice;
		[ObservableProperty] private int _radioOutputDevice = -1;
		[ObservableProperty] private string _radioPttMethod = "Vox";
		[ObservableProperty] private string _radioSerialPort = "";
		[ObservableProperty] private int _radioCm108VendorId = Cm108Compatibility.CMediaVendorId;
		[ObservableProperty] private int _radioCm108ProductId;
		[ObservableProperty] private int _radioCm108GpioPin = 3;
		[ObservableProperty] private HidDeviceInfo _selectedCm108Device;

		// The CM108 ids as typed (hex), so an interface that is not plugged in, or not in the known list, can still be set.
		[ObservableProperty]
		[CustomValidation(typeof(ConfigurationViewModel), nameof(ValidateCm108VendorIdText))]
		[NotifyDataErrorInfo]
		private string _radioCm108VendorIdText = Cm108Compatibility.FormatUsbId(Cm108Compatibility.CMediaVendorId);

		[ObservableProperty]
		[CustomValidation(typeof(ConfigurationViewModel), nameof(ValidateCm108ProductIdText))]
		[NotifyDataErrorInfo]
		private string _radioCm108ProductIdText = Cm108Compatibility.FormatUsbId(0);

		public static ValidationResult ValidateCm108VendorIdText(string value, ValidationContext context) =>
			Cm108Compatibility.TryParseUsbId(value, out var id) && id != 0
				? ValidationResult.Success
				: new ValidationResult("Enter the vendor id in hex, 0x0001–0xFFFF (C-Media 0x0D8C, AIOC 0x1209).",
					context?.MemberName == null ? null : new[] { context.MemberName });

		public static ValidationResult ValidateCm108ProductIdText(string value, ValidationContext context) =>
			Cm108Compatibility.TryParseUsbId(value, out _)
				? ValidationResult.Success
				: new ValidationResult("Enter the product id in hex, 0x0000–0xFFFF (0x0000 = any product, AIOC 0x7388).",
					context?.MemberName == null ? null : new[] { context.MemberName });

		// Set while a picked device writes its ids, so the id handlers don't re-select (and briefly clear) the picker.
		private bool _applyingCm108Device;
		[ObservableProperty] private string _radioCarrierDetect = "None";
		[ObservableProperty] private bool _radioCarrierDetectInverted;
		[ObservableProperty] private bool _radioCourtesyTone = true;
		[ObservableProperty] private int _radioTxLeadMs;
		[ObservableProperty] private int _radioTxHangMs = 300;
		[ObservableProperty] private int _radioTxTailMs = 150;
		[ObservableProperty] private int _radioMaxTransmitSeconds = 180;
		[ObservableProperty] private bool _radioAntiLoop = true;
		[ObservableProperty] private double _radioTxGain = 1.0;
		[ObservableProperty] private double _radioRxGain = 1.0;
		[ObservableProperty] private int _radioHighPassCutoffHz = 250;
		[ObservableProperty] private bool _radioRecordWhileBridging;

		// Squelch
		[ObservableProperty] private string _squelchMode = "Vox";
		[ObservableProperty] private double _squelchOpenDbfs = -38;
		[ObservableProperty] private double _squelchCloseDbfs = -45;
		[ObservableProperty] private int _squelchHangMs = 600;
		[ObservableProperty] private double _squelchCtcssFrequency;

		// Emergency
		[ObservableProperty] private bool _emergencyDetectMdc1200;
		[ObservableProperty] private bool _emergencyDetectTones;
		[ObservableProperty] private bool _emergencyCreateCall;
		[ObservableProperty] private int _emergencyCallPriority = 1;

		// Transmit test
		[ObservableProperty] private bool _isTestingTransmit;
		[ObservableProperty] private string _transmitTestStatus = "";

		// ─────────── Recorder ───────────
		[ObservableProperty] private string _recorderChannel = "all";
		[ObservableProperty] private string _recorderDepartmentId = "";
		[ObservableProperty] private string _recorderStore = "local";
		[ObservableProperty] private string _recorderLocalPath = "";
		[ObservableProperty] private string _recorderLog = "jsonl";
		[ObservableProperty] private string _recorderLogPath = "";

		// S3
		[ObservableProperty] private string _s3Endpoint = "";
		[ObservableProperty] private string _s3Bucket = "";
		[ObservableProperty] private string _s3Region = "";
		[ObservableProperty] private string _s3Prefix = "relay-recordings";
		[ObservableProperty] private bool _s3ForcePathStyle;
		[ObservableProperty] private bool _s3UseSsl = true;
		[ObservableProperty] private string _s3AccessKey = "";
		[ObservableProperty] private string _s3SecretKey = "";

		// ─────────── Dispatch voice ───────────
		[ObservableProperty] private string _dispatchChannel = "default";
		[ObservableProperty] private string _dispatchDepartmentId = "";
		[ObservableProperty] private int _dispatchPollSeconds = 15;
		[ObservableProperty] private int _dispatchGiveUpAfterMinutes = 10;

		// ─────────── TTS ───────────
		[ObservableProperty] private string _ttsServiceBaseUrl = "";
		[ObservableProperty] private string _ttsVoice = "";
		[ObservableProperty] private int _ttsSpeed;

		// ─────────── Reconnect (voice modes) ───────────
		[ObservableProperty] private bool _resilienceKeepRetrying = true;
		[ObservableProperty] private int _resilienceMaxConsecutiveFailures = 5;
		[ObservableProperty] private double _resilienceMaxBackoffSeconds = 60;

		// ─────────── Telemetry ───────────
		[ObservableProperty] private string _telemetryEnvironment = "";
		[ObservableProperty] private string _sentryDsn = "";
		[ObservableProperty] private string _countlyUrl = "";
		[ObservableProperty] private string _countlyAppKey = "";

		// ─────────── Secret reveal toggle ───────────
		[ObservableProperty] private bool _revealSecrets;

		// ─────────── Tune meter ───────────
		[ObservableProperty] private bool _isTuning;
		[ObservableProperty] private double _tuneDbfs = -80;
		[ObservableProperty] private bool _tuneSquelchOpen;
		[ObservableProperty] private string _tuneStatus = "";

		/// <summary>Tune is only available on Windows (NAudio capture + the engine RadioMode tuner).</summary>
		public bool CanTune => OperatingSystem.IsWindows();

#if NET10_0_WINDOWS
		private CancellationTokenSource _tuneCts;
		private CancellationTokenSource _transmitCts;

		// The latest tune run: completes once it has closed the capture device and given the radio claim back.
		private Task _tuneRun = Task.CompletedTask;
#endif

		/// <summary>Setup advice for the selected radio profile.</summary>
		public string RadioProfileNotes => SelectedRadioProfile?.SetupNotes ?? "";

		/// <summary>True when the saved settings name a cellular PTT profile (drives the key-up lead hint).</summary>
		public bool IsPocProfile => RadioProfiles.Find(RadioProfileKey).IsPushToTalkOverCellular;

		/// <summary>The CM108 ids in effect and the interface family they name, e.g. "VID 0x1209, PID 0x7388 (AIOC)".</summary>
		public string Cm108Summary
		{
			get
			{
				var ids = Cm108Compatibility.FormatIds(RadioCm108VendorId, RadioCm108ProductId);
				var family = RadioCm108ProductId == 0
					? Cm108Compatibility.KnownInterfaces.FirstOrDefault(i => i.VendorId == RadioCm108VendorId)
					: Cm108Compatibility.FindInterface(RadioCm108VendorId, RadioCm108ProductId);
				return family != null ? $"{ids} ({family.Name})" : $"{ids} (not a known CM108-compatible interface)";
			}
		}

		protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);
			if (_loading || e.PropertyName == null)
				return;

			// Only edits of a setting dirty the form: status, progress and derived/read-only properties (a Save or Reload
			// re-announces LoadError and friends right after clearing IsDirty) must not bring the banner back.
			switch (e.PropertyName)
			{
				case nameof(IsDirty):
				case nameof(LoadError):
				case nameof(HasLoadError):
				case nameof(HasEnvOverrides):
				case nameof(UserConfigPathDisplay):
				case nameof(RadioProfileNotes):
				case nameof(IsPocProfile):
				case nameof(Cm108Summary):
				case nameof(StatusMessage):
				case nameof(ConnectionTestStatus):
				case nameof(IsTestingConnection):
				case nameof(TransmitTestStatus):
				case nameof(IsTestingTransmit):
				case nameof(IsTuning):
				case nameof(TuneDbfs):
				case nameof(TuneSquelchOpen):
				case nameof(TuneStatus):
				case nameof(RevealSecrets):
				case nameof(SelectedRadioProfile):
				case nameof(HasErrors):
				case nameof(Title):
					return;
				default:
					IsDirty = true;
					break;
			}
		}

		partial void OnSelectedRadioProfileChanged(RadioProfile value) => OnPropertyChanged(nameof(RadioProfileNotes));

		partial void OnRadioProfileKeyChanged(string value) => OnPropertyChanged(nameof(IsPocProfile));

		partial void OnRadioCm108VendorIdChanged(int value) => OnCm108IdsChanged();

		partial void OnRadioCm108ProductIdChanged(int value) => OnCm108IdsChanged();

		partial void OnRadioCm108VendorIdTextChanged(string value)
		{
			if (Cm108Compatibility.TryParseUsbId(value, out var id) && id != 0)
				RadioCm108VendorId = id;
		}

		partial void OnRadioCm108ProductIdTextChanged(string value)
		{
			if (Cm108Compatibility.TryParseUsbId(value, out var id))
				RadioCm108ProductId = id;
		}

		/// <summary>Keeps the typed ids, the summary and the device picker in step with the ids in effect.</summary>
		private void OnCm108IdsChanged()
		{
			OnPropertyChanged(nameof(Cm108Summary));

			// Rewrite a field only when it doesn't already say this id, so typing "1209" isn't reformatted under the caret.
			if (!Cm108Compatibility.TryParseUsbId(RadioCm108VendorIdText, out var vendorId) || vendorId != RadioCm108VendorId)
				RadioCm108VendorIdText = Cm108Compatibility.FormatUsbId(RadioCm108VendorId);
			if (!Cm108Compatibility.TryParseUsbId(RadioCm108ProductIdText, out var productId) || productId != RadioCm108ProductId)
				RadioCm108ProductIdText = Cm108Compatibility.FormatUsbId(RadioCm108ProductId);

			if (!_applyingCm108Device)
				SelectedCm108Device = FindCm108Device();
		}

		/// <summary>The listed device with exactly the ids in effect (none for product id 0: no device reports it).</summary>
		private HidDeviceInfo FindCm108Device() =>
			Cm108Devices.FirstOrDefault(d => d.VendorId == RadioCm108VendorId && d.ProductId == RadioCm108ProductId);

		partial void OnSelectedCm108DeviceChanged(HidDeviceInfo value)
		{
			if (value == null || _loading)
				return;
			_applyingCm108Device = true;
			try
			{
				RadioCm108VendorId = value.VendorId;
				RadioCm108ProductId = value.ProductId;
			}
			finally
			{
				_applyingCm108Device = false;
			}
		}

		// ─────────── Load / Save ───────────

		private void LoadFromOptions(RelayHostOptions o)
		{
			_loading = true;
			try
			{
				Mode = o.Mode ?? "smtp";
				AudioConfigPath = o.AudioConfigPath ?? "";

				var api = o.Resgrid ?? new ResgridApiClientOptions();
				ResgridBaseUrl = api.BaseUrl ?? "";
				ResgridClientId = api.ClientId ?? "";
				ResgridClientSecret = MaskIfPresent(api.ClientSecret);
				ResgridRefreshToken = MaskIfPresent(api.RefreshToken);
				ResgridSystemApiKey = MaskIfPresent(api.SystemApiKey);
				ResgridDepartmentId = api.DepartmentId ?? "";
				ResgridGrantType = api.GrantType;
				ResgridServiceAccount = api.ServiceAccount;

				var smtp = o.Smtp ?? new SmtpRelayOptions();
				SmtpServerName = smtp.ServerName ?? "";
				SmtpPort = smtp.Port;
				SmtpDepartmentAddressDomains = JoinDomains(smtp.DepartmentAddressDomains);
				SmtpGroupAddressDomains = JoinDomains(smtp.GroupAddressDomains);
				SmtpGroupMessageAddressDomains = JoinDomains(smtp.GroupMessageAddressDomains);
				SmtpListAddressDomains = JoinDomains(smtp.ListAddressDomains);
				SmtpDataDirectory = smtp.DataDirectory ?? "";
				SmtpDefaultDepartmentId = smtp.DefaultDepartmentId ?? "";
				SmtpResolveDispatchCodes = smtp.ResolveDispatchCodes;
				SmtpValidateDepartmentCodes = smtp.ValidateDepartmentCodes;
				SmtpDefaultCallPriority = smtp.DefaultCallPriority;
				SmtpDefaultCallType = smtp.DefaultCallType ?? "";
				SmtpDuplicateWindowHours = smtp.DuplicateWindowHours;
				SmtpSaveRawMessages = smtp.SaveRawMessages;
				SmtpHostedMode = smtp.HostedMode;
				var redis = smtp.RedisCache ?? new RedisCacheOptions();
				RedisEnabled = redis.Enabled;
				RedisConnectionString = redis.ConnectionString ?? "";
				RedisTtlMinutes = redis.TtlMinutes;

				var voice = o.Voice ?? new VoiceConnectionOptions();
				VoiceChannel = voice.Channel ?? "default";
				VoiceDepartmentId = voice.DepartmentId ?? "";
				VoiceEnforceSeatLimit = voice.EnforceSeatLimit;

				var radio = o.Radio ?? new RadioModeOptions();
				RadioProfileKey = RadioProfiles.Find(radio.Profile).Key;
				SelectedRadioProfile = RadioProfiles.Find(radio.Profile);
				RadioInputDevice = radio.InputDevice;
				RadioOutputDevice = radio.OutputDevice;
				RadioPttMethod = string.IsNullOrWhiteSpace(radio.PttMethod) ? "Vox" : radio.PttMethod;
				RadioSerialPort = radio.SerialPort ?? "";
				RadioCm108VendorId = radio.Cm108VendorId;
				RadioCm108ProductId = radio.Cm108ProductId;
				// Set the fields too: an unchanged id raises no change, and a field may hold an invalid edit being reloaded over.
				RadioCm108VendorIdText = Cm108Compatibility.FormatUsbId(radio.Cm108VendorId);
				RadioCm108ProductIdText = Cm108Compatibility.FormatUsbId(radio.Cm108ProductId);
				RadioCm108GpioPin = radio.Cm108GpioPin;
				SelectedCm108Device = FindCm108Device();
				RadioCarrierDetect = string.IsNullOrWhiteSpace(radio.CarrierDetect) ? "None" : radio.CarrierDetect;
				RadioCarrierDetectInverted = radio.CarrierDetectInverted;
				RadioCourtesyTone = radio.CourtesyTone;
				RadioTxLeadMs = radio.TxLeadMs;
				RadioTxHangMs = radio.TxHangMs;
				RadioTxTailMs = radio.TxTailMs;
				RadioMaxTransmitSeconds = radio.MaxTransmitSeconds;
				RadioAntiLoop = radio.AntiLoop;
				RadioTxGain = radio.TxGain;
				RadioRxGain = radio.RxGain;
				RadioHighPassCutoffHz = radio.HighPassCutoffHz;
				RadioRecordWhileBridging = radio.RecordWhileBridging;

				var sq = radio.Squelch ?? new SquelchSettings();
				SquelchMode = sq.Mode.ToString();
				SquelchOpenDbfs = sq.OpenDbfs;
				SquelchCloseDbfs = sq.CloseDbfs;
				SquelchHangMs = sq.HangMs;
				SquelchCtcssFrequency = sq.CtcssFrequency;

				var em = radio.Emergency ?? new EmergencyOptions();
				EmergencyDetectMdc1200 = em.DetectMdc1200;
				EmergencyDetectTones = em.DetectTones;
				EmergencyCreateCall = em.CreateCall;
				EmergencyCallPriority = em.CallPriority;

				var rec = o.Recorder ?? new RecorderModeOptions();
				RecorderChannel = rec.Channel ?? "all";
				RecorderDepartmentId = rec.DepartmentId ?? "";
				RecorderStore = string.IsNullOrWhiteSpace(rec.Store) ? "local" : rec.Store.Trim().ToLowerInvariant();
				RecorderLocalPath = rec.LocalPath ?? "";
				RecorderLog = string.IsNullOrWhiteSpace(rec.Log) ? "jsonl" : rec.Log.Trim().ToLowerInvariant();
				RecorderLogPath = rec.LogPath ?? "";

				var s3 = rec.S3 ?? new S3StorageOptions();
				S3Endpoint = s3.Endpoint ?? "";
				S3Bucket = s3.Bucket ?? "";
				S3Region = s3.Region ?? "";
				S3Prefix = s3.Prefix ?? "";
				S3ForcePathStyle = s3.ForcePathStyle;
				S3UseSsl = s3.UseSsl;
				S3AccessKey = MaskIfPresent(s3.AccessKey);
				S3SecretKey = MaskIfPresent(s3.SecretKey);

				var dv = o.DispatchVoice ?? new DispatchVoiceOptions();
				DispatchChannel = dv.Channel ?? "default";
				DispatchDepartmentId = dv.DepartmentId ?? "";
				DispatchPollSeconds = dv.PollSeconds;
				DispatchGiveUpAfterMinutes = dv.GiveUpAfterMinutes;

				var tts = o.Tts ?? new Resgrid.Audio.Voice.ToneOut.TtsSettings();
				TtsServiceBaseUrl = tts.ServiceBaseUrl ?? "";
				TtsVoice = tts.Voice ?? "";
				TtsSpeed = tts.Speed;

				var resilience = o.Resilience ?? new ResilienceOptions();
				ResilienceKeepRetrying = resilience.KeepRetrying;
				ResilienceMaxConsecutiveFailures = resilience.MaxConsecutiveFailures;
				ResilienceMaxBackoffSeconds = resilience.MaxBackoffSeconds;

				var tel = o.Telemetry ?? new RelayTelemetryOptions();
				TelemetryEnvironment = tel.Environment ?? "";
				SentryDsn = MaskIfPresent(tel.Sentry?.Dsn);
				CountlyUrl = tel.Countly?.Url ?? "";
				CountlyAppKey = MaskIfPresent(tel.Countly?.AppKey);
			}
			finally
			{
				_loading = false;
			}

			ValidateAllProperties();
			IsDirty = false;
		}

		/// <summary>Writes the form back into <paramref name="o"/>, preserving sentinel-held secrets.</summary>
		private void ApplyToOptions(RelayHostOptions o)
		{
			o.Mode = Mode;
			o.AudioConfigPath = AudioConfigPath;

			o.Resgrid ??= new ResgridApiClientOptions();
			o.Resgrid.BaseUrl = ResgridBaseUrl?.Trim();
			o.Resgrid.ClientId = ResgridClientId?.Trim();
			o.Resgrid.ClientSecret = MergeSecret(ResgridClientSecret, o.Resgrid.ClientSecret);
			o.Resgrid.RefreshToken = MergeSecret(ResgridRefreshToken, o.Resgrid.RefreshToken);
			o.Resgrid.SystemApiKey = MergeSecret(ResgridSystemApiKey, o.Resgrid.SystemApiKey);
			o.Resgrid.DepartmentId = ResgridDepartmentId?.Trim();
			o.Resgrid.GrantType = ResgridGrantType;
			o.Resgrid.ServiceAccount = ResgridServiceAccount;

			o.Smtp ??= new SmtpRelayOptions();
			o.Smtp.ServerName = SmtpServerName;
			o.Smtp.Port = SmtpPort;
			o.Smtp.DepartmentAddressDomains = SplitDomains(SmtpDepartmentAddressDomains);
			o.Smtp.GroupAddressDomains = SplitDomains(SmtpGroupAddressDomains);
			o.Smtp.GroupMessageAddressDomains = SplitDomains(SmtpGroupMessageAddressDomains);
			o.Smtp.ListAddressDomains = SplitDomains(SmtpListAddressDomains);
			o.Smtp.DataDirectory = SmtpDataDirectory;
			o.Smtp.DefaultDepartmentId = SmtpDefaultDepartmentId?.Trim();
			o.Smtp.ResolveDispatchCodes = SmtpResolveDispatchCodes;
			o.Smtp.ValidateDepartmentCodes = SmtpValidateDepartmentCodes;
			o.Smtp.DefaultCallPriority = SmtpDefaultCallPriority;
			o.Smtp.DefaultCallType = SmtpDefaultCallType;
			o.Smtp.DuplicateWindowHours = SmtpDuplicateWindowHours;
			o.Smtp.SaveRawMessages = SmtpSaveRawMessages;
			o.Smtp.HostedMode = SmtpHostedMode;
			o.Smtp.RedisCache ??= new RedisCacheOptions();
			o.Smtp.RedisCache.Enabled = RedisEnabled;
			o.Smtp.RedisCache.ConnectionString = RedisConnectionString;
			o.Smtp.RedisCache.TtlMinutes = RedisTtlMinutes;

			o.Voice ??= new VoiceConnectionOptions();
			o.Voice.Channel = VoiceChannel;
			o.Voice.DepartmentId = VoiceDepartmentId?.Trim();
			o.Voice.EnforceSeatLimit = VoiceEnforceSeatLimit;

			o.Radio ??= new RadioModeOptions();
			o.Radio.Profile = RadioProfileKey;
			o.Radio.InputDevice = RadioInputDevice;
			o.Radio.OutputDevice = RadioOutputDevice;
			o.Radio.PttMethod = RadioPttMethod;
			o.Radio.SerialPort = RadioSerialPort?.Trim();
			o.Radio.Cm108VendorId = RadioCm108VendorId;
			o.Radio.Cm108ProductId = RadioCm108ProductId;
			o.Radio.Cm108GpioPin = Math.Clamp(RadioCm108GpioPin, 1, 8);
			o.Radio.CarrierDetect = RadioCarrierDetect;
			o.Radio.CarrierDetectInverted = RadioCarrierDetectInverted;
			o.Radio.CourtesyTone = RadioCourtesyTone;
			o.Radio.TxLeadMs = Math.Clamp(RadioTxLeadMs, 0, 3000);
			o.Radio.TxHangMs = Math.Max(0, RadioTxHangMs);
			o.Radio.TxTailMs = Math.Max(0, RadioTxTailMs);
			o.Radio.MaxTransmitSeconds = Math.Max(0, RadioMaxTransmitSeconds);
			o.Radio.AntiLoop = RadioAntiLoop;
			o.Radio.TxGain = RadioTxGain;
			o.Radio.RxGain = RadioRxGain;
			o.Radio.HighPassCutoffHz = Math.Max(0, RadioHighPassCutoffHz);
			o.Radio.RecordWhileBridging = RadioRecordWhileBridging;
			o.Radio.Squelch ??= new SquelchSettings();
			if (Enum.TryParse<SquelchMode>(SquelchMode, ignoreCase: true, out var sqMode))
				o.Radio.Squelch.Mode = sqMode;
			o.Radio.Squelch.OpenDbfs = SquelchOpenDbfs;
			o.Radio.Squelch.CloseDbfs = SquelchCloseDbfs;
			o.Radio.Squelch.HangMs = SquelchHangMs;
			o.Radio.Squelch.CtcssFrequency = SquelchCtcssFrequency;
			o.Radio.Emergency ??= new EmergencyOptions();
			o.Radio.Emergency.DetectMdc1200 = EmergencyDetectMdc1200;
			o.Radio.Emergency.DetectTones = EmergencyDetectTones;
			o.Radio.Emergency.CreateCall = EmergencyCreateCall;
			o.Radio.Emergency.CallPriority = EmergencyCallPriority;

			o.Recorder ??= new RecorderModeOptions();
			o.Recorder.Channel = RecorderChannel;
			o.Recorder.DepartmentId = RecorderDepartmentId?.Trim();
			o.Recorder.Store = RecorderStore;
			o.Recorder.LocalPath = RecorderLocalPath;
			o.Recorder.Log = RecorderLog;
			o.Recorder.LogPath = RecorderLogPath;
			o.Recorder.S3 ??= new S3StorageOptions();
			o.Recorder.S3.Endpoint = S3Endpoint;
			o.Recorder.S3.Bucket = S3Bucket;
			o.Recorder.S3.Region = S3Region;
			o.Recorder.S3.Prefix = S3Prefix;
			o.Recorder.S3.ForcePathStyle = S3ForcePathStyle;
			o.Recorder.S3.UseSsl = S3UseSsl;
			o.Recorder.S3.AccessKey = MergeSecret(S3AccessKey, o.Recorder.S3.AccessKey);
			o.Recorder.S3.SecretKey = MergeSecret(S3SecretKey, o.Recorder.S3.SecretKey);

			o.DispatchVoice ??= new DispatchVoiceOptions();
			o.DispatchVoice.Channel = DispatchChannel;
			o.DispatchVoice.DepartmentId = DispatchDepartmentId?.Trim();
			o.DispatchVoice.PollSeconds = Math.Max(5, DispatchPollSeconds);
			o.DispatchVoice.GiveUpAfterMinutes = Math.Max(1, DispatchGiveUpAfterMinutes);

			o.Tts ??= new Resgrid.Audio.Voice.ToneOut.TtsSettings();
			o.Tts.ServiceBaseUrl = TtsServiceBaseUrl?.Trim();
			o.Tts.Voice = TtsVoice;
			o.Tts.Speed = TtsSpeed;

			o.Resilience ??= new ResilienceOptions();
			o.Resilience.KeepRetrying = ResilienceKeepRetrying;
			o.Resilience.MaxConsecutiveFailures = Math.Max(1, ResilienceMaxConsecutiveFailures);
			o.Resilience.MaxBackoffSeconds = Math.Max(1, ResilienceMaxBackoffSeconds);

			o.Telemetry ??= new RelayTelemetryOptions();
			o.Telemetry.Environment = TelemetryEnvironment;
			o.Telemetry.Sentry ??= new SentryTelemetryOptions();
			o.Telemetry.Sentry.Dsn = MergeSecret(SentryDsn, o.Telemetry.Sentry.Dsn);
			o.Telemetry.Countly ??= new CountlyTelemetryOptions();
			o.Telemetry.Countly.Url = CountlyUrl;
			o.Telemetry.Countly.AppKey = MergeSecret(CountlyAppKey, o.Telemetry.Countly.AppKey);
		}

		/// <summary>
		/// The on-disk layers (appsettings.json + relay.user.json, no environment), or defaults when they can't be read — with
		/// the reason in <paramref name="error"/>. Does not change the form's load state; see <see cref="LoadFormFromDisk"/>.
		/// </summary>
		private static RelayHostOptions LoadDiskModel(out string error)
		{
			try
			{
				var options = RelayConfiguration.LoadFromDisk();
				error = null;
				return options;
			}
			catch (RelayConfigurationException ex)
			{
				error = ex.Message;
				return new RelayHostOptions();
			}
		}

		/// <summary>
		/// Repopulates the form from the on-disk layers, keeping why they could not be read in <see cref="_diskLoadError"/>
		/// (shown in the banner; Save refuses to write the defaults then shown over the file until a Reload reads it).
		/// </summary>
		private void LoadFormFromDisk()
		{
			var options = LoadDiskModel(out var error);
			_diskLoadError = error;
			LoadFromOptions(options);
		}

		private void NotifyLoadState()
		{
			OnPropertyChanged(nameof(HasEnvOverrides));
			OnPropertyChanged(nameof(LoadError));
			OnPropertyChanged(nameof(HasLoadError));
		}

		/// <summary>The edited form overlaid on the on-disk settings, without saving; for tests that must use unsaved edits.</summary>
		private RelayHostOptions BuildWorkingOptions()
		{
			var disk = LoadDiskModel(out _);
			ApplyToOptions(disk);

			// What will actually run: the edited form plus the environment layer (which wins at run time).
			var options = RelayConfiguration.WithEnvironment(disk);

			// Resgrid rotates the refresh token on every use: a test must share the running relay's token cache, or the
			// rotated token would only live in memory and the saved one would be spent.
			if (options.Resgrid != null && string.IsNullOrWhiteSpace(options.Resgrid.TokenCachePath))
				options.Resgrid.TokenCachePath = _configuration.Current.Resgrid?.TokenCachePath;
			return options;
		}

		// Not while a connection test runs: its rotated refresh token is only handed to the relay's cache by a Save made after
		// the test has signed in (CarryForwardTestedSignIn), so a Save mid-test left it stranded in the pending cache.
		private bool CanSave() => !IsTestingConnection;

		[RelayCommand(CanExecute = nameof(CanSave))]
		private void Save()
		{
			if (IsTestingConnection)
			{
				StatusMessage = "Wait for the connection test to finish, then Save.";
				return;
			}

			ValidateAllProperties();
			if (HasErrors)
			{
				StatusMessage = "Cannot save — fix the highlighted required fields.";
				return;
			}

			if (_diskLoadError != null)
			{
				// The form holds defaults, not the file: saving would replace every setting the file has with them — even once
				// the file is readable again, until a Reload shows what it holds.
				NotifyLoadState();
				StatusMessage = $"Cannot save — the settings files could not be read ({_diskLoadError}), so this form shows defaults. " +
					$"Fix {UserConfigPathDisplay} (Open settings folder), or delete it to start over, then Reload.";
				return;
			}

			// Start from a disk-only snapshot (no RESGRID__RELAY__ env overrides) so unedited/unknown fields survive without
			// baking environment values into the user config file.
			var options = LoadDiskModel(out var readError);
			if (readError != null)
			{
				// The file broke after the form was loaded: the snapshot is defaults, and saving would drop its other settings.
				StatusMessage = $"Cannot save — the settings files could not be read ({readError}). Fix {UserConfigPathDisplay} " +
					"(Open settings folder), or delete it to start over, then Reload.";
				return;
			}
			ApplyToOptions(options);

			// Pin the token cache the desktop app uses, so a console host on the same account (which reads this file too) shares
			// the rotated refresh token instead of starting from the configured one, which Resgrid has already spent.
			if (options.Resgrid != null && string.IsNullOrWhiteSpace(options.Resgrid.TokenCachePath))
				options.Resgrid.TokenCachePath = RelayConfiguration.DefaultTokenCachePath();

			try
			{
				// Validate what will run (a secret may come from the environment only), but save just the disk model.
				RelayConfiguration.WithEnvironment(options).Resgrid.Validate();
			}
			catch (InvalidOperationException ex)
			{
				StatusMessage = $"Cannot save — {ex.Message}";
				return;
			}

			try
			{
				_configuration.Save(options);
			}
			catch (Exception ex)
			{
				StatusMessage = $"Could not save: {ex.Message}";
				return;
			}

			var signInNote = CarryForwardTestedSignIn();

			// Re-load so masked secrets reflect the now-stored values.
			LoadFormFromDisk();
			NotifyLoadState();
			var running = RelayModeLauncher.AllModes.Where(_launcher.IsActive).Select(RelayModeLauncher.FriendlyName).ToList();
			StatusMessage = (running.Count == 0
				? $"Saved to {_configuration.UserConfigPath}"
				: $"Saved. Restart {string.Join(", ", running)} (Operations) to apply the changes.") + signInNote;
		}

		[RelayCommand]
		private void Reload()
		{
			_configuration.Reload();
			LoadFormFromDisk();
			NotifyLoadState();
			StatusMessage = "Reloaded from disk.";
		}

		// ─────────── Radio profile ───────────

		[RelayCommand]
		private void ApplyRadioProfile()
		{
			var profile = SelectedRadioProfile ?? RadioProfiles.Custom;
			var radio = new RadioModeOptions();
			ApplyToOptionsRadioOnly(radio);
			profile.ApplyTo(radio);

			RadioProfileKey = profile.Key;
			if (profile.AppliesSettings)
			{
				RadioTxLeadMs = radio.TxLeadMs;
				RadioTxHangMs = radio.TxHangMs;
				RadioTxTailMs = radio.TxTailMs;
				RadioCourtesyTone = radio.CourtesyTone;
				RadioAntiLoop = radio.AntiLoop;
				RadioHighPassCutoffHz = radio.HighPassCutoffHz;
				SquelchMode = radio.Squelch.Mode.ToString();
				SquelchOpenDbfs = radio.Squelch.OpenDbfs;
				SquelchCloseDbfs = radio.Squelch.CloseDbfs;
				SquelchHangMs = radio.Squelch.HangMs;
				EmergencyDetectMdc1200 = radio.Emergency.DetectMdc1200;
				EmergencyDetectTones = radio.Emergency.DetectTones;
			}

			// PTT keying is wiring, not a radio setting: it is never changed here (switching Vox to Cm108 silently broke a radio
			// keyed by a serial line). A cellular radio still needs a hard line, so say so; the Radio Bridge start refuses Vox.
			if (!profile.AppliesSettings)
				StatusMessage = "Custom profile: settings left as entered.";
			else if (profile.IsPushToTalkOverCellular && string.Equals(RadioPttMethod, "Vox", StringComparison.OrdinalIgnoreCase))
				StatusMessage = $"Applied the {profile.DisplayName} profile. It needs a hard PTT line: set PTT keying to the interface " +
					"the radio is wired to (Cm108, SerialRts or SerialDtr), review the radio settings, then Save.";
			else
				StatusMessage = $"Applied the {profile.DisplayName} profile. Review the radio settings, then Save.";
		}

		private static void ApplyToOptionsRadioOnly(RadioModeOptions radio)
		{
			radio.Squelch = new SquelchSettings();
			radio.Emergency = new EmergencyOptions();
		}

		[RelayCommand]
		private void RefreshDevices()
		{
			RefreshDeviceLists();
			StatusMessage = $"Found {InputDevices.Count} input, {OutputDevices.Count - 1} output, {SerialPorts.Count} serial and {Cm108Devices.Count} CM108 device(s).";
		}

		private void RefreshDeviceLists()
		{
			var wasLoading = _loading;
			_loading = true;
			try
			{
				var input = RadioInputDevice;
				var output = RadioOutputDevice;
				var serial = RadioSerialPort;

				InputDevices.Clear();
				foreach (var d in _devices.GetInputDevices())
					InputDevices.Add(d);
				OutputDevices.Clear();
				foreach (var d in _devices.GetOutputDevices())
					OutputDevices.Add(d);
				SerialPorts.Clear();
				foreach (var p in _devices.GetSerialPorts())
					SerialPorts.Add(p.PortName);
				Cm108Devices.Clear();
				foreach (var h in _devices.GetCm108Devices())
					Cm108Devices.Add(h);

				// Re-assert the selections the lists were rebuilt under.
				RadioInputDevice = -2;
				RadioInputDevice = input;
				RadioOutputDevice = -2;
				RadioOutputDevice = output;
				RadioSerialPort = serial;
				SelectedCm108Device = FindCm108Device();
			}
			finally
			{
				_loading = wasLoading;
			}
		}

		// ─────────── Connection test ───────────

		[RelayCommand]
		private async Task TestConnection()
		{
			if (IsTestingConnection)
				return;

			IsTestingConnection = true;
			ConnectionTestStatus = "Connecting to Resgrid…";
			var handBackNote = "";
			try
			{
				var api = BuildWorkingOptions().Resgrid;
				api.Validate();
				var usesPendingCache = UseTestTokenCache(api);

				bool healthy;
				try
				{
					healthy = await Task.Run(async () =>
					{
						using var client = new ResgridV4ApiClient(api);
						using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
						return await client.IsHealthyAsync(timeout.Token).ConfigureAwait(false);
					}).ConfigureAwait(true);
				}
				finally
				{
					handBackNote = ReturnTestedRelayChain();
				}

				ConnectionTestStatus = healthy
					? $"Connected: signed in to {api.BaseUrl} with the {api.GrantType} grant." + (usesPendingCache
						? " Resgrid has rotated this refresh token: Save before leaving this page so the relay gets the new one."
						: "")
					: "Signed in, but the Resgrid API health check did not succeed.";
			}
			catch (OperationCanceledException)
			{
				ConnectionTestStatus = "Connection test timed out after 30 seconds.";
			}
			catch (Exception ex)
			{
				ConnectionTestStatus = $"Connection failed: {ex.Message}";
			}
			finally
			{
				ConnectionTestStatus += handBackNote;
				IsTestingConnection = false;
				if (_disposed)
					DiscardPendingSignIn(); // the page closed mid-test; nobody can save this sign-in now (a relay chain still to hand back is kept)
			}
		}

		// The sign-in identity whose rotated refresh token the connection test keeps in _pendingTokenCachePath (an edited
		// credential set the relay does not run with yet), or null.
		private string _pendingTokenIdentity;
		private string _pendingTokenCachePath;

		// While a test of an edited client secret runs on a copy of the relay's chain (TokenCacheFork), and after it until the
		// rotated chain is handed back: the relay's token cache and the refresh token copied from it (null when it had none), or
		// null. Still set after the test means the hand-back failed and the pending cache holds the relay's only live token.
		private string _forkedRelayCachePath;
		private string _forkedRefreshToken;

		/// <summary>
		/// Points the connection test's token cache at the right place. The relay's cache holds no record of whose token it
		/// is, so borrowing it for other credentials either passed on the relay's live user token (a wrong client secret
		/// "connected") or wrote the test's tokens over the relay's rotated refresh token.
		/// <list type="bullet">
		/// <item>ClientCredentials / SystemApiKey: no cache — the edited id and secret are exchanged for real, and nothing is written.</item>
		/// <item>RefreshToken with the relay's own identity (base URL, client id, refresh token) and client secret: the relay's
		/// cache, which holds the live rotated token.</item>
		/// <item>RefreshToken with the relay's identity but an edited client secret: a copy of the relay's cache without its access
		/// token (which would pass without the secret ever being sent), so the edited secret has to redeem the relay's refresh
		/// token; the rotated chain is handed back to the relay's cache after the test (<see cref="ReturnTestedRelayChain"/>).</item>
		/// <item>RefreshToken with an edited identity: a separate pending cache, carried into the relay's cache when that identity
		/// is saved (<see cref="CarryForwardTestedSignIn"/>). Returns true.</item>
		/// </list>
		/// On the pending cache the test must redeem (<see cref="ResgridApiClientOptions.RequireTokenRedemption"/>): an access token
		/// an earlier test left there, or in the API client's memory, was issued for another client secret.
		/// </summary>
		private bool UseTestTokenCache(ResgridApiClientOptions api)
		{
			if (api.GrantType != ResgridAuthGrantType.RefreshToken)
			{
				api.TokenCachePath = null;
				return false;
			}

			// A failed hand-back left the relay's only live refresh token in the pending cache: it goes back before this test writes
			// there, or redeems the relay's spent one.
			if (_forkedRelayCachePath != null)
			{
				var stillPending = ReturnTestedRelayChain();
				if (_forkedRelayCachePath != null)
					throw new IOException(stillPending.Trim());
			}

			var relay = _configuration.Current?.Resgrid;
			var relayChain = relay != null && relay.GrantType == ResgridAuthGrantType.RefreshToken && SignInIdentity(api) == SignInIdentity(relay);
			if (relayChain && string.Equals((api.ClientSecret ?? "").Trim(), (relay.ClientSecret ?? "").Trim(), StringComparison.Ordinal))
			{
				api.TokenCachePath = relay.TokenCachePath;
				return false;
			}

			var relayCache = Path.GetFullPath(string.IsNullOrWhiteSpace(relay?.TokenCachePath) ? RelayConfiguration.DefaultTokenCachePath() : relay.TokenCachePath);
			_pendingTokenCachePath = Path.Combine(Path.GetDirectoryName(relayCache) ?? ".", "resgrid-token.pending.json");
			if (relayChain)
			{
				_forkedRefreshToken = TokenCacheFork.Fork(relayCache, _pendingTokenCachePath);
				_forkedRelayCachePath = relayCache;
			}
			_pendingTokenIdentity = SignInIdentity(api);
			api.TokenCachePath = _pendingTokenCachePath;
			api.RequireTokenRedemption = true;
			return !relayChain;
		}

		/// <summary>
		/// After a test of an edited client secret on a copy of the relay's chain: hands the chain the test rotated back to the
		/// relay's token cache, since the relay's own refresh token is spent by then. Returns a note for the status line, or "".
		/// </summary>
		private string ReturnTestedRelayChain()
		{
			var relayCache = _forkedRelayCachePath;
			if (relayCache == null)
				return "";

			try
			{
				TokenCacheFork.HandBack(_pendingTokenCachePath, relayCache, _forkedRefreshToken);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				// Kept, with what a retry needs, as the pending sign-in of the relay's own identity: a Save carries it over
				// (CarryForwardTestedSignIn), and the next test or the page closing hands it back first (never deletes it).
				return $" The rotated refresh token could not be handed back to the relay's token cache ({ex.Message}); Save to retry.";
			}

			_forkedRelayCachePath = null;
			_forkedRefreshToken = null;
			_pendingTokenIdentity = null;
			return "";
		}

		/// <summary>The refresh-token chain a sign-in uses: base URL, client id and the configured refresh token.</summary>
		private static string SignInIdentity(ResgridApiClientOptions api) =>
			string.Join("|",
				(api.BaseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant(),
				(api.ClientId ?? "").Trim(),
				(api.RefreshToken ?? "").Trim());

		/// <summary>
		/// After a Save: when the saved settings now run with the identity the connection test signed in with, moves the
		/// test's rotated refresh token into the relay's token cache (Resgrid has already spent the one that was typed in).
		/// Returns a note for the status line, or "".
		/// </summary>
		private string CarryForwardTestedSignIn()
		{
			if (_pendingTokenIdentity == null || string.IsNullOrWhiteSpace(_pendingTokenCachePath) || !File.Exists(_pendingTokenCachePath))
				return "";

			var saved = _configuration.Current?.Resgrid;
			if (saved == null || saved.GrantType != ResgridAuthGrantType.RefreshToken || string.IsNullOrWhiteSpace(saved.TokenCachePath)
				|| SignInIdentity(saved) != _pendingTokenIdentity)
				return "";

			try
			{
				var target = Path.GetFullPath(saved.TokenCachePath);
				var directory = Path.GetDirectoryName(target);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);
				File.Move(_pendingTokenCachePath, target, overwrite: true);
				_pendingTokenIdentity = null;
				_forkedRelayCachePath = null; // a relay chain whose hand-back failed has now reached the relay
				_forkedRefreshToken = null;
				return "";
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return $" The tested sign-in could not be moved into the relay's token cache ({ex.Message}); paste a fresh refresh token if the relay cannot sign in.";
			}
		}

		private void DiscardPendingSignIn()
		{
			// The relay's chain from a test whose hand-back failed is not an unused sign-in: the relay's own token is spent, so this
			// file is the only live one. Try the hand-back again; until it succeeds the file stays.
			if (_forkedRelayCachePath != null)
			{
				ReturnTestedRelayChain();
				if (_forkedRelayCachePath != null)
					return;
			}

			_pendingTokenIdentity = null;
			if (string.IsNullOrWhiteSpace(_pendingTokenCachePath))
				return;
			try { File.Delete(_pendingTokenCachePath); }
			catch { /* best effort: it only holds a token nobody saved */ }
		}

		// ─────────── Audio watcher file ───────────

		[RelayCommand]
		private void OpenAudioConfig()
		{
			try
			{
				var path = AudioConfigPath;
				if (string.IsNullOrWhiteSpace(path))
				{
					StatusMessage = "Set the watcher file path first.";
					return;
				}

				if (!Path.IsPathRooted(path))
					path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
				if (!File.Exists(path))
				{
					StatusMessage = $"{path} does not exist yet. Create it with the console 'setup' command.";
					return;
				}

				Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				StatusMessage = $"Could not open the watcher file: {ex.Message}";
			}
		}

		[RelayCommand]
		private void OpenSettingsFolder()
		{
			try
			{
				var folder = Path.GetDirectoryName(_configuration.UserConfigPath);
				if (!string.IsNullOrWhiteSpace(folder))
				{
					Directory.CreateDirectory(folder);
					Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
				}
			}
			catch (Exception ex)
			{
				StatusMessage = $"Could not open the settings folder: {ex.Message}";
			}
		}

		// ─────────── Transmit test ───────────

		// AllowConcurrentExecutions so the button stays enabled to cancel a running test.
		[RelayCommand(AllowConcurrentExecutions = true)]
		private async Task TestTransmit()
		{
#if NET10_0_WINDOWS
			if (IsTestingTransmit)
			{
				try { _transmitCts?.Cancel(); } catch { /* disposed */ }
				return;
			}

			if (IsTuning)
				StopTune();

			// A tune holds the radio until it has closed its capture device; it is stopping, so wait for it to give the radio back.
			if (!_tuneRun.IsCompleted)
			{
				await _tuneRun.ConfigureAwait(true);
				if (IsTestingTransmit)
					return; // another click started the test while this one waited
			}

			// The page may have closed while the tune gave the radio back: nothing would cancel a test started now.
			if (_disposed)
				return;

			RelayHostOptions options;
			try
			{
				options = BuildWorkingOptions();
			}
			catch (Exception ex)
			{
				// An unreadable setting (e.g. a bad RESGRID__RELAY__ value) is this test's failure to report, not an unhandled one.
				TransmitTestStatus = $"Transmit test failed: {ex.Message}";
				return;
			}

			// Claim the radio for the whole test, cleanup included: the Radio Bridge cannot start until the claim is released
			// (after PTT is released and the devices are closed), and the test cannot start while the bridge runs or starts.
			var claim = _launcher.TryClaimRadio(out var busy);
			if (claim == null)
			{
				TransmitTestStatus = busy;
				return;
			}

			_transmitCts?.Dispose();
			_transmitCts = new CancellationTokenSource();
			IsTestingTransmit = true;
			var lead = options.Radio.PttMethod?.Equals("Vox", StringComparison.OrdinalIgnoreCase) == true ? 0 : Math.Clamp(options.Radio.TxLeadMs, 0, 3000);
			TransmitTestStatus = $"Keying the radio: {lead} ms lead, then a 2-second 1 kHz tone…";
			try
			{
				var token = _transmitCts.Token;
				// The token also goes to Task.Run, so a cancel while the work is still queued never reaches the hardware (the
				// engine checks it again right before keying).
				await Task.Run(() => RadioMode.RunTransmitTestAsync(options, Log.Logger, token), token).ConfigureAwait(true);
				TransmitTestStatus = "Done. A listener on the far side should have heard the whole 2-second tone; if the start was cut off, raise the key-up lead.";
			}
			catch (PttReleaseException ex)
			{
				// Whether the test finished, failed or was cancelled, the radio may still be on the air.
				TransmitTestStatus = $"Transmit test failed: {ex.Message}";
			}
			catch (OperationCanceledException)
			{
				// Only thrown once PTT is confirmed released, or before it was ever keyed.
				TransmitTestStatus = "Transmit test cancelled; PTT released.";
			}
			catch (Exception ex)
			{
				TransmitTestStatus = $"Transmit test failed: {ex.Message}";
			}
			finally
			{
				claim.Dispose();
				IsTestingTransmit = false;
				_transmitCts?.Dispose();
				_transmitCts = null;
			}
#else
			TransmitTestStatus = "The transmit test is only available on Windows.";
			await Task.CompletedTask;
#endif
		}

		// ─────────── Tune meter ───────────

		// AllowConcurrentExecutions so the command (and its bound button) stays enabled while the
		// long-running tune meter is active — otherwise the stop branch would be unreachable.
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

			if (_launcher.IsActive("radio"))
			{
				TuneStatus = "The Radio Bridge is running: its live receive meter is on the Dashboard. Stop it to tune here.";
				return;
			}

			if (!_tuneRun.IsCompleted)
			{
				TuneStatus = "The previous tune is still closing the receive device; try again in a moment.";
				return;
			}

			// Tune against an isolated snapshot with the edited form values overlaid, so uncommitted
			// form changes never mutate the shared Current that active services read.
			RelayHostOptions options;
			try
			{
				options = BuildWorkingOptions();
			}
			catch (Exception ex)
			{
				TuneStatus = $"Tune failed: {ex.Message}";
				return;
			}

			// Claim the radio for the whole tune, cleanup included: the Radio Bridge cannot start (and open the same receive
			// device) until the capture device is closed, and the tune cannot start while a bridge is tracked, starting or stopping.
			var claim = _launcher.TryClaimRadio(out var busy);
			if (claim == null)
			{
				TuneStatus = busy;
				return;
			}

			// Each run owns its cancellation source: a stopped run still unwinding must not clear or dispose a newer run's.
			var tuneCts = new CancellationTokenSource();
			var tuneRun = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			_tuneCts = tuneCts;
			_tuneRun = tuneRun.Task;
			IsTuning = true;
			TuneStatus = "Tuning… key a radio on the channel to see levels. Squelch should open only on speech.";

			var progress = new Progress<TuneSample>(sample =>
			{
				TuneDbfs = sample.Dbfs;
				TuneSquelchOpen = sample.SquelchOpen;
			});

			try
			{
				await RadioMode.RunTuneAsync(options, Log.Logger, tuneCts.Token, progress).ConfigureAwait(true);
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
				// RunTuneAsync has closed the capture device by now (also when stopped): only now give the radio back. A Stop
				// only cancels; the claim is held until here.
				claim.Dispose();
				if (ReferenceEquals(_tuneCts, tuneCts))
				{
					IsTuning = false;
					_tuneCts = null;
				}
				tuneCts.Dispose();
				tuneRun.TrySetResult(true);
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
		/// Resolves an edited secret field against the stored value: an unchanged sentinel keeps the stored secret; otherwise
		/// the typed value (including a deliberate blank) wins.
		/// </summary>
		private static string MergeSecret(string edited, string stored)
			=> edited == SecretSentinel ? stored : edited?.Trim();

		private static string JoinDomains(string[] domains)
			=> domains == null ? "" : string.Join(", ", domains);

		private static string[] SplitDomains(string csv)
		{
			if (string.IsNullOrWhiteSpace(csv))
				return Array.Empty<string>();

			return csv.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(p => p.Trim().ToLowerInvariant())
				.Where(p => p.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			StopTune();
#if NET10_0_WINDOWS
			// The running transmit test and tune unwind on their own (PTT released, devices closed), dispose their own
			// cancellation sources and only then give the radio back to the launcher, so the Radio Bridge can't start on top of
			// their cleanup.
			try { _transmitCts?.Cancel(); } catch { /* disposed */ }
#endif
			// A connection test still running hands back or discards its own sign-in when it ends: deleting a copy of the relay's
			// chain under it would have it redeem the configured, already spent refresh token. (A relay chain whose hand-back
			// failed is retried here, and kept if it fails again.)
			if (!IsTestingConnection)
				DiscardPendingSignIn();
		}
	}
}
