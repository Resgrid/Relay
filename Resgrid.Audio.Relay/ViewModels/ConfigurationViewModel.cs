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

		public ConfigurationViewModel(ConfigurationService configuration, DeviceEnumerationService devices, RelayModeLauncher launcher)
		{
			_configuration = configuration;
			_devices = devices;
			_launcher = launcher;
			RefreshDeviceLists();
			// The form edits the on-disk layers only, so a Save never writes RESGRID__RELAY__ environment values into the
			// user file (the banner says when the environment overrides what is shown).
			LoadFromOptions(_configuration.LoadFromDisk());
		}

		/// <summary>Why the settings files could not be read (defaults are shown), or null.</summary>
		public string LoadError => _configuration.LoadError;

		public bool HasLoadError => !string.IsNullOrEmpty(_configuration.LoadError);

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

		[ObservableProperty]
		private ResgridAuthGrantType _resgridGrantType = ResgridAuthGrantType.RefreshToken;

		[ObservableProperty]
		private bool _resgridServiceAccount;

		[ObservableProperty]
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
		[ObservableProperty] private int _radioCm108VendorId = 0x0D8C;
		[ObservableProperty] private int _radioCm108ProductId;
		[ObservableProperty] private int _radioCm108GpioPin = 3;
		[ObservableProperty] private HidDeviceInfo _selectedCm108Device;
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
#endif

		/// <summary>Setup advice for the selected radio profile.</summary>
		public string RadioProfileNotes => SelectedRadioProfile?.SetupNotes ?? "";

		/// <summary>True when the saved settings name a cellular PTT profile (drives the key-up lead hint).</summary>
		public bool IsPocProfile => RadioProfiles.Find(RadioProfileKey).IsPushToTalkOverCellular;

		public string Cm108Summary => $"VID 0x{RadioCm108VendorId:X4}, PID {(RadioCm108ProductId == 0 ? "any C-Media" : "0x" + RadioCm108ProductId.ToString("X4"))}";

		protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);
			if (_loading || e.PropertyName == null)
				return;

			switch (e.PropertyName)
			{
				case nameof(IsDirty):
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

		partial void OnRadioCm108VendorIdChanged(int value) => OnPropertyChanged(nameof(Cm108Summary));

		partial void OnRadioCm108ProductIdChanged(int value) => OnPropertyChanged(nameof(Cm108Summary));

		partial void OnSelectedCm108DeviceChanged(HidDeviceInfo value)
		{
			if (value == null || _loading)
				return;
			RadioCm108VendorId = value.VendorId;
			RadioCm108ProductId = value.ProductId;
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
				RadioCm108GpioPin = radio.Cm108GpioPin;
				SelectedCm108Device = Cm108Devices.FirstOrDefault(d => d.VendorId == radio.Cm108VendorId && d.ProductId == radio.Cm108ProductId);
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

		/// <summary>The edited form overlaid on the on-disk settings, without saving; for tests that must use unsaved edits.</summary>
		private RelayHostOptions BuildWorkingOptions()
		{
			var disk = _configuration.LoadFromDisk();
			ApplyToOptions(disk);

			// What will actually run: the edited form plus the environment layer (which wins at run time).
			var options = RelayConfiguration.WithEnvironment(disk);

			// Resgrid rotates the refresh token on every use: a test must share the running relay's token cache, or the
			// rotated token would only live in memory and the saved one would be spent.
			if (options.Resgrid != null && string.IsNullOrWhiteSpace(options.Resgrid.TokenCachePath))
				options.Resgrid.TokenCachePath = _configuration.Current.Resgrid?.TokenCachePath;
			return options;
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

			// Start from a disk-only snapshot (no RESGRID__RELAY__ env overrides) so unedited/unknown fields survive without
			// baking environment values into the user config file.
			var options = _configuration.LoadFromDisk();
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

			// Re-load so masked secrets reflect the now-stored values.
			LoadFromOptions(_configuration.LoadFromDisk());
			OnPropertyChanged(nameof(LoadError));
			OnPropertyChanged(nameof(HasLoadError));
			var running = RelayModeLauncher.AllModes.Where(_launcher.IsActive).Select(RelayModeLauncher.FriendlyName).ToList();
			StatusMessage = running.Count == 0
				? $"Saved to {_configuration.UserConfigPath}"
				: $"Saved. Restart {string.Join(", ", running)} (Operations) to apply the changes.";
		}

		[RelayCommand]
		private void Reload()
		{
			_configuration.Reload();
			LoadFromOptions(_configuration.LoadFromDisk());
			OnPropertyChanged(nameof(HasEnvOverrides));
			OnPropertyChanged(nameof(LoadError));
			OnPropertyChanged(nameof(HasLoadError));
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
				if (profile.IsPushToTalkOverCellular && string.Equals(RadioPttMethod, "Vox", StringComparison.OrdinalIgnoreCase))
					RadioPttMethod = "Cm108";
			}

			StatusMessage = profile.AppliesSettings
				? $"Applied the {profile.DisplayName} profile. Review the radio settings, then Save."
				: "Custom profile: settings left as entered.";
		}

		private void ApplyToOptionsRadioOnly(RadioModeOptions radio)
		{
			radio.PttMethod = RadioPttMethod;
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
				SelectedCm108Device = Cm108Devices.FirstOrDefault(d => d.VendorId == RadioCm108VendorId && d.ProductId == RadioCm108ProductId);
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
			try
			{
				var api = BuildWorkingOptions().Resgrid;
				api.Validate();

				var healthy = await Task.Run(async () =>
				{
					using var client = new ResgridV4ApiClient(api);
					using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
					return await client.IsHealthyAsync(timeout.Token).ConfigureAwait(false);
				}).ConfigureAwait(true);

				ConnectionTestStatus = healthy
					? $"Connected: signed in to {api.BaseUrl} with the {api.GrantType} grant."
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
				IsTestingConnection = false;
			}
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

			if (_launcher.IsActive("radio"))
			{
				TransmitTestStatus = "Stop the Radio Bridge first — it already owns the radio's audio and PTT.";
				return;
			}

			var options = BuildWorkingOptions();
			_transmitCts?.Dispose();
			_transmitCts = new CancellationTokenSource();
			IsTestingTransmit = true;
			var lead = options.Radio.PttMethod?.Equals("Vox", StringComparison.OrdinalIgnoreCase) == true ? 0 : Math.Clamp(options.Radio.TxLeadMs, 0, 3000);
			TransmitTestStatus = $"Keying the radio: {lead} ms lead, then a 2-second 1 kHz tone…";
			try
			{
				var token = _transmitCts.Token;
				await Task.Run(() => RadioMode.RunTransmitTestAsync(options, Log.Logger, token)).ConfigureAwait(true);
				TransmitTestStatus = "Done. A listener on the far side should have heard the whole 2-second tone; if the start was cut off, raise the key-up lead.";
			}
			catch (OperationCanceledException)
			{
				TransmitTestStatus = "Transmit test cancelled; PTT released.";
			}
			catch (Exception ex)
			{
				TransmitTestStatus = $"Transmit test failed: {ex.Message}";
			}
			finally
			{
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

			// Tune against an isolated snapshot with the edited form values overlaid, so uncommitted
			// form changes never mutate the shared Current that active services read.
			var options = BuildWorkingOptions();

			_tuneCts?.Dispose();
			_tuneCts = new CancellationTokenSource();
			IsTuning = true;
			TuneStatus = "Tuning… key a radio on the channel to see levels. Squelch should open only on speech.";

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
			try { _transmitCts?.Cancel(); } catch { /* disposed */ }
			_tuneCts?.Dispose();
#endif
		}
	}
}
