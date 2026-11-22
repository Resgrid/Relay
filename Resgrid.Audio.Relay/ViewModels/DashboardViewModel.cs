using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Audio.Relay.Views;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Dashboard: an overall summary, a getting-started checklist while setup is incomplete or nothing is running, and one
	/// live tile per running service (connection pills, receive meter, squelch/TX/RX lamps, counters, last fault).
	/// </summary>
	public partial class DashboardViewModel : ObservableObject, IDisposable
	{
		private readonly RelayController _controller;
		private readonly RelayModeLauncher _launcher;
		private readonly ConfigurationService _configuration;
		private readonly DesktopSettingsService _desktop;
		private readonly NavigationService _navigation;
		private bool _disposed;

		public DashboardViewModel(RelayController controller, RelayModeLauncher launcher, ConfigurationService configuration,
			DesktopSettingsService desktop, NavigationService navigation)
		{
			_controller = controller;
			_launcher = launcher;
			_configuration = configuration;
			_desktop = desktop;
			_navigation = navigation;

			_controller.RunningServices.CollectionChanged += OnRunningServicesChanged;
			_controller.OverallStateChanged += OnOverallStateChanged;

			RebuildTiles();
			UpdateSummary();
		}

		[ObservableProperty]
		private string _title = "Dashboard";

		/// <summary>Worst-of running-service health, mirrored from the controller.</summary>
		[ObservableProperty]
		private RelayServiceState _overallState = RelayServiceState.Stopped;

		[ObservableProperty]
		private string _summaryText = "No services running";

		[ObservableProperty]
		private int _runningCount;

		/// <summary>True when nothing is running.</summary>
		[ObservableProperty]
		private bool _isIdle = true;

		/// <summary>True while the getting-started checklist should be shown.</summary>
		[ObservableProperty]
		private bool _showSetup = true;

		/// <summary>One tile per running service. Bound by the Dashboard view.</summary>
		public ObservableCollection<ServiceTileViewModel> Tiles { get; } = new ObservableCollection<ServiceTileViewModel>();

		public ObservableCollection<SetupStepViewModel> SetupSteps { get; } = new ObservableCollection<SetupStepViewModel>();

		[RelayCommand]
		private async System.Threading.Tasks.Task StartDefaults()
		{
			var problems = await _launcher.StartDefaultModesAsync().ConfigureAwait(true);
			if (problems.Count > 0)
				SummaryText = problems[0];
		}

		private void OnRunningServicesChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			RebuildTiles();
			UpdateSummary();
		}

		private void OnOverallStateChanged(object sender, EventArgs e)
		{
			foreach (var tile in Tiles)
				tile.Refresh();
			UpdateSummary();
		}

		private void RebuildTiles()
		{
			foreach (var tile in Tiles)
				tile.Dispose();
			Tiles.Clear();

			foreach (var service in _controller.RunningServices)
				Tiles.Add(new ServiceTileViewModel(service, _launcher));
		}

		private void UpdateSummary()
		{
			OverallState = _controller.OverallState;
			RunningCount = _controller.RunningServices.Count;
			IsIdle = RunningCount == 0;

			if (RunningCount == 0)
				SummaryText = "No services running";
			else
			{
				var modes = string.Join(", ", _controller.RunningServices.Select(s => RelayModeLauncher.FriendlyName(s.Mode)));
				SummaryText = RunningCount == 1 ? $"1 service running ({modes})" : $"{RunningCount} services running ({modes})";
			}

			BuildSetupSteps();
		}

		private void BuildSetupSteps()
		{
			var o = _configuration.Current;
			bool apiOk;
			try
			{
				(o.Resgrid ?? new ResgridApiClientOptions()).Validate();
				apiOk = true;
			}
			catch (InvalidOperationException)
			{
				apiOk = false;
			}

			var defaultModes = _launcher.DefaultModes();
			bool modeReady = defaultModes.Any(m => _launcher.Validate(m).Count == 0);
			bool autoStart = _desktop.Current.AutoStartModes.Count > 0;
			bool running = RunningCount > 0;

			SetupSteps.Clear();
			SetupSteps.Add(new SetupStepViewModel(1, "Connect Relay to Resgrid",
				"Enter the API URL, client id and secret and a refresh token under Configuration › Resgrid API, then use Test connection.",
				apiOk, "Open configuration", () => _navigation.Navigate(typeof(ConfigurationView))));
			SetupSteps.Add(new SetupStepViewModel(2, "Set up what this machine does",
				"SMTP dispatch, a radio bridge (analog radio or an LTE donor radio such as Rapid Radios or Diga-Talk+), the recorder or dispatch tone-out. " +
				"Each mode's pre-flight check on the Operations screen lists anything missing.",
				modeReady, "Open configuration", () => _navigation.Navigate(typeof(ConfigurationView))));
			SetupSteps.Add(new SetupStepViewModel(3, "Choose what starts automatically",
				"Turn on Auto-start for the modes this machine should always run, and optionally start Relay at Windows sign-in.",
				autoStart, "Open operations", () => _navigation.Navigate(typeof(OperationsView))));
			SetupSteps.Add(new SetupStepViewModel(4, "Start the relay",
				"Start the default modes here, from the footer, or per mode on the Operations screen.",
				running, "Open operations", () => _navigation.Navigate(typeof(OperationsView))));

			ShowSetup = !running || !(apiOk && modeReady && autoStart);
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			_controller.RunningServices.CollectionChanged -= OnRunningServicesChanged;
			_controller.OverallStateChanged -= OnOverallStateChanged;

			foreach (var tile in Tiles)
				tile.Dispose();
			Tiles.Clear();
		}
	}

	/// <summary>One step of the Dashboard's getting-started checklist.</summary>
	public partial class SetupStepViewModel : ObservableObject
	{
		private readonly Action _action;

		public SetupStepViewModel(int number, string title, string detail, bool isDone, string actionText, Action action)
		{
			Number = number;
			Title = title;
			Detail = detail;
			IsDone = isDone;
			ActionText = actionText;
			_action = action;
		}

		public int Number { get; }
		public string Title { get; }
		public string Detail { get; }
		public bool IsDone { get; }
		public string ActionText { get; }
		public string Marker => IsDone ? "✓" : Number.ToString();

		[RelayCommand]
		private void Go() => _action?.Invoke();
	}
}
