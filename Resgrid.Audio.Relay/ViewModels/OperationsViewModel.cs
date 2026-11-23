using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Operations: a card per relay mode (state, last fault, pre-flight check, Start/Stop/Restart and auto-start toggle) plus
	/// the app's startup preferences (launch at Windows sign-in, close to tray, fault notifications). All starting and
	/// stopping goes through <see cref="RelayModeLauncher"/>.
	/// </summary>
	public partial class OperationsViewModel : ObservableObject, IDisposable
	{
		private readonly RelayController _controller;
		private readonly RelayModeLauncher _launcher;
		private readonly DesktopSettingsService _desktop;
		private bool _disposed;
		private bool _loading;

		public OperationsViewModel(RelayController controller, RelayModeLauncher launcher, DesktopSettingsService desktop)
		{
			_controller = controller;
			_launcher = launcher;
			_desktop = desktop;

			_loading = true;
			LaunchAtSignIn = desktop.LaunchAtSignIn;
			CloseToTray = desktop.Current.CloseToTray;
			NotifyOnFault = desktop.Current.NotifyOnFault;
			_loading = false;

			foreach (var mode in RelayModeLauncher.AllModes)
				Modes.Add(new ModeCardViewModel(mode, launcher, desktop, this));

			_controller.RunningServices.CollectionChanged += OnRunningServicesChanged;
			_controller.OverallStateChanged += OnOverallStateChanged;

			RefreshCards();
		}

		[ObservableProperty]
		private string _title = "Operations";

		[ObservableProperty]
		private string _statusMessage = "";

		[ObservableProperty]
		private bool _launchAtSignIn;

		[ObservableProperty]
		private bool _closeToTray = true;

		[ObservableProperty]
		private bool _notifyOnFault = true;

		public ObservableCollection<ModeCardViewModel> Modes { get; } = new ObservableCollection<ModeCardViewModel>();

		partial void OnLaunchAtSignInChanged(bool value)
		{
			if (_loading)
				return;
			_desktop.LaunchAtSignIn = value;
			StatusMessage = value
				? "Relay will start minimized to the tray when you sign in to Windows."
				: "Relay will no longer start at sign-in.";
		}

		partial void OnCloseToTrayChanged(bool value)
		{
			if (_loading)
				return;
			_desktop.Current.CloseToTray = value;
			_desktop.Save();
		}

		partial void OnNotifyOnFaultChanged(bool value)
		{
			if (_loading)
				return;
			_desktop.Current.NotifyOnFault = value;
			_desktop.Save();
		}

		[RelayCommand]
		private async Task StartAll()
		{
			var problems = await _launcher.StartDefaultModesAsync().ConfigureAwait(true);
			StatusMessage = problems.Count == 0 ? "Started." : string.Join(Environment.NewLine, problems);
		}

		[RelayCommand]
		private async Task StopAll()
		{
			await _controller.StopAllAsync().ConfigureAwait(true);
			StatusMessage = "All modes stopped.";
		}

		internal void ReportStatus(string message) => StatusMessage = message ?? "";

		private void OnRunningServicesChanged(object sender, NotifyCollectionChangedEventArgs e) => RefreshCards();
		private void OnOverallStateChanged(object sender, EventArgs e) => RefreshCards();

		private void RefreshCards()
		{
			foreach (var card in Modes)
				card.Refresh();
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
