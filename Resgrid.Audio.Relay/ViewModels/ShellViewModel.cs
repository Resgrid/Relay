using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// View-model for the shell window: the overall health light, the status text, the footer Start/Stop buttons (which
	/// run the default modes through <see cref="RelayModeLauncher"/>) and the tray tooltip.
	/// </summary>
	public partial class ShellViewModel : ObservableObject
	{
		private readonly RelayController _controller;
		private readonly RelayModeLauncher _launcher;
		private string _lastProblem;

		public ShellViewModel(RelayController controller, RelayModeLauncher launcher)
		{
			_controller = controller;
			_launcher = launcher;
			_controller.OverallStateChanged += OnOverallStateChanged;
			UpdateState();
		}

		public RelayController Controller => _controller;

		[ObservableProperty]
		private RelayServiceState _overallState = RelayServiceState.Stopped;

		[ObservableProperty]
		private bool _isRunning;

		[ObservableProperty]
		private string _statusText = "Stopped";

		[ObservableProperty]
		private string _trayToolTip = "Resgrid Relay — stopped";

		/// <summary>Starts the default modes (auto-start set, or the configured mode). Disabled once the app is quitting.</summary>
		[RelayCommand(CanExecute = nameof(CanStart))]
		private async Task Start()
		{
			var problems = await _launcher.StartDefaultModesAsync().ConfigureAwait(true);
			foreach (var problem in problems)
				Serilog.Log.Warning("Start: {Problem}", problem);

			// The footer has room for one line: the first problem, plus how many more the Logs screen lists.
			_lastProblem = problems.Count == 0
				? null
				: problems.Count == 1 ? problems[0] : $"{problems[0]} (+{problems.Count - 1} more in Logs)";
			UpdateState();
		}

		private bool CanStart() => !_controller.IsShuttingDown;

		/// <summary>Stops every running mode.</summary>
		[RelayCommand]
		private async Task Stop()
		{
			_lastProblem = null;
			await _controller.StopAllAsync().ConfigureAwait(true);
			UpdateState();
		}

		private void OnOverallStateChanged(object sender, EventArgs e) => UpdateState();

		private void UpdateState()
		{
			OverallState = _controller.OverallState;
			IsRunning = _controller.IsRunning;
			StartCommand.NotifyCanExecuteChanged();

			var running = _controller.RunningServices.ToList();
			if (running.Count == 0)
			{
				StatusText = _lastProblem ?? "Stopped";
				TrayToolTip = "Resgrid Relay — stopped";
				return;
			}

			// A mode that failed to start beside ones that did still needs its reason shown, not just the ones running.
			var parts = running.Select(s => $"{RelayModeLauncher.FriendlyName(s.Mode)}: {s.State}");
			StatusText = string.Join("   ·   ", parts) + (_lastProblem != null ? "   ·   " + _lastProblem : "");
			// Windows truncates tray tooltips at 127 characters.
			var tip = "Resgrid Relay — " + string.Join(", ", running.Select(s => $"{RelayModeLauncher.FriendlyName(s.Mode)} {s.State}"));
			TrayToolTip = tip.Length > 120 ? tip.Substring(0, 120) + "…" : tip;
		}
	}
}
