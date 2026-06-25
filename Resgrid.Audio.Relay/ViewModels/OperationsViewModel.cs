using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Resgrid.Audio.Relay.Services;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Operations: a Start/Stop card per relay mode. A card's live state is derived from whether
	/// <see cref="RelayController.RunningServices"/> currently holds a service with that mode.
	/// Start clones the loaded <see cref="RelayHostOptions"/> (so the shared instance is never
	/// mutated), forces the card's <c>Mode</c>, and hands it to <see cref="RelayController.Start"/>;
	/// Stop calls <see cref="RelayController.StopAsync"/> on the matching running service. Each
	/// card also shows a short pre-flight validation summary mirroring the console's
	/// <c>ValidateOptions</c> (plus a couple of voice-mode checks).
	/// </summary>
	public partial class OperationsViewModel : ObservableObject, IDisposable
	{
		private static readonly string[] AllModes = { "smtp", "audio", "radio", "record", "dispatch" };

		private readonly RelayController _controller;
		private readonly ConfigurationService _configuration;
		private bool _disposed;

		public OperationsViewModel(RelayController controller, ConfigurationService configuration)
		{
			_controller = controller;
			_configuration = configuration;

			foreach (var mode in AllModes)
				Modes.Add(new ModeCardViewModel(mode, this));

			_controller.RunningServices.CollectionChanged += OnRunningServicesChanged;
			_controller.OverallStateChanged += OnOverallStateChanged;

			RefreshCards();
		}

		[ObservableProperty]
		private string _title = "Operations";

		public ObservableCollection<ModeCardViewModel> Modes { get; } = new ObservableCollection<ModeCardViewModel>();

		public RelayController Controller => _controller;
		public ConfigurationService Configuration => _configuration;

		private void OnRunningServicesChanged(object sender, NotifyCollectionChangedEventArgs e) => RefreshCards();
		private void OnOverallStateChanged(object sender, EventArgs e) => RefreshCards();

		private void RefreshCards()
		{
			foreach (var card in Modes)
				card.Refresh();
		}

		private IRelayService FindRunning(string mode)
		{
			return _controller.RunningServices.FirstOrDefault(s =>
				string.Equals(s.Mode, mode, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>Current lifecycle state for a mode, or Stopped when not running.</summary>
		internal RelayServiceState StateFor(string mode)
		{
			var service = FindRunning(mode);
			return service?.State ?? RelayServiceState.Stopped;
		}

		internal bool IsRunning(string mode) => FindRunning(mode) != null;

		/// <summary>Builds the options for a mode by deep-cloning the current config and forcing Mode.</summary>
		internal RelayHostOptions BuildOptions(string mode)
		{
			var clone = CloneOptions(_configuration.Current);
			clone.Mode = mode;
			return clone;
		}

		internal void StartMode(string mode)
		{
			// Don't start a duplicate or an invalid configuration: keep pre-flight failures in the
			// card's validation summary rather than starting a doomed service (or letting Start throw).
			if (IsRunning(mode) || Validate(mode).Count > 0)
				return;

			_controller.Start(BuildOptions(mode));
		}

		internal async Task StopModeAsync(string mode)
		{
			var service = FindRunning(mode);
			if (service != null)
				await _controller.StopAsync(service).ConfigureAwait(true);
		}

		/// <summary>
		/// Pre-flight validation for a mode. Replicates the key checks from the console's
		/// <c>ValidateOptions</c> (Resgrid creds + grant-type requirements, SMTP dispatch
		/// domains/Redis) and adds a couple of voice-mode checks (dispatch needs a TTS URL,
		/// record-to-S3 needs a bucket). Returns the list of problems; empty means ready.
		/// </summary>
		internal IReadOnlyList<string> Validate(string mode)
		{
			var o = _configuration.Current;
			var errors = new List<string>();

			if (string.IsNullOrWhiteSpace(o.Resgrid?.BaseUrl))
				errors.Add("Resgrid API base URL is required.");
			if (string.IsNullOrWhiteSpace(o.Resgrid?.ClientId))
				errors.Add("Resgrid API client id is required.");
			if (string.IsNullOrWhiteSpace(o.Resgrid?.ClientSecret))
				errors.Add("Resgrid API client secret is required.");

			if (o.Resgrid != null)
			{
				switch (o.Resgrid.GrantType)
				{
					case ResgridAuthGrantType.RefreshToken:
						if (string.IsNullOrWhiteSpace(o.Resgrid.RefreshToken) && string.IsNullOrWhiteSpace(o.Resgrid.TokenCachePath))
							errors.Add("A refresh token (or token cache path) is required for the RefreshToken grant.");
						break;
					case ResgridAuthGrantType.SystemApiKey:
						if (string.IsNullOrWhiteSpace(o.Resgrid.SystemApiKey))
							errors.Add("A system API key is required for the SystemApiKey grant.");
						break;
				}
			}

			if (mode == "smtp")
			{
				var hasDomains = (o.Smtp?.DepartmentAddressDomains?.Length ?? 0) > 0
					|| (o.Smtp?.GroupAddressDomains?.Length ?? 0) > 0
					|| (o.Smtp?.GroupMessageAddressDomains?.Length ?? 0) > 0
					|| (o.Smtp?.ListAddressDomains?.Length ?? 0) > 0;
				if (!hasDomains)
					errors.Add("At least one SMTP dispatch domain must be configured.");

				if (o.Smtp?.RedisCache is { Enabled: true } && string.IsNullOrWhiteSpace(o.Smtp.RedisCache.ConnectionString))
					errors.Add("Redis cache is enabled but the connection string is empty.");
			}

			if (mode == "dispatch")
			{
				if (string.IsNullOrWhiteSpace(o.Tts?.ServiceBaseUrl))
					errors.Add("Dispatch tone-out needs a TTS service URL (Tts.ServiceBaseUrl).");
			}

			if (mode == "record")
			{
				var store = o.Recorder?.Store ?? "local";
				if ((store == "s3" || store == "both") && string.IsNullOrWhiteSpace(o.Recorder?.S3?.Bucket))
					errors.Add("Recorder S3 storage selected but no S3 bucket is configured.");
			}

			return errors;
		}

		private static RelayHostOptions CloneOptions(RelayHostOptions source)
		{
			// Deep clone via JSON so per-mode starts never mutate the shared Current instance.
			var json = JsonSerializer.Serialize(source);
			return JsonSerializer.Deserialize<RelayHostOptions>(json) ?? new RelayHostOptions();
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			_controller.RunningServices.CollectionChanged -= OnRunningServicesChanged;
			_controller.OverallStateChanged -= OnOverallStateChanged;
		}
	}
}
