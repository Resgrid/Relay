using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// A single mode card on the Operations screen: live state, the last fault message, the pre-flight check, the
	/// auto-start toggle and Start/Stop/Restart. State is re-read from the controller on <see cref="Refresh"/>.
	/// </summary>
	public partial class ModeCardViewModel : ObservableObject
	{
		private readonly RelayModeLauncher _launcher;
		private readonly DesktopSettingsService _desktop;
		private readonly OperationsViewModel _owner;
		private bool _loading;

		public ModeCardViewModel(string mode, RelayModeLauncher launcher, DesktopSettingsService desktop, OperationsViewModel owner)
		{
			Mode = mode;
			_launcher = launcher;
			_desktop = desktop;
			_owner = owner;
			DisplayName = RelayModeLauncher.FriendlyName(mode);
			Description = DescriptionFor(mode);

			_loading = true;
			AutoStart = desktop.IsAutoStart(mode);
			_loading = false;

			Refresh();
		}

		/// <summary>The mode key passed to the controller (e.g. "radio").</summary>
		public string Mode { get; }

		public string DisplayName { get; }

		public string Description { get; }

		[ObservableProperty]
		private RelayServiceState _state = RelayServiceState.Stopped;

		/// <summary>True while a non-faulted instance is running (drives Start vs Stop).</summary>
		[ObservableProperty]
		private bool _isRunning;

		[ObservableProperty]
		private string _stateText = "Stopped";

		[ObservableProperty]
		private string _lastError;

		[ObservableProperty]
		private bool _hasLastError;

		[ObservableProperty]
		private bool _autoStart;

		[ObservableProperty]
		private bool _isBusy;

		/// <summary>Pre-flight problems for this mode (empty when ready to start).</summary>
		public ObservableCollectionLite<string> ValidationErrors { get; } = new ObservableCollectionLite<string>();

		[ObservableProperty]
		private bool _hasValidationErrors;

		[ObservableProperty]
		private string _validationSummary = "Ready";

		partial void OnAutoStartChanged(bool value)
		{
			if (!_loading)
				_desktop.SetAutoStart(Mode, value);
		}

		/// <summary>Re-reads derived state. Called when the running set or a service state changes.</summary>
		public void Refresh()
		{
			var service = _launcher.Controller.Find(Mode);
			State = service?.State ?? RelayServiceState.Stopped;
			IsRunning = service != null && State != RelayServiceState.Faulted && State != RelayServiceState.Stopped;

			var startedAt = _launcher.Controller.StartedAtUtc(Mode);
			StateText = service == null
				? "Stopped"
				: IsRunning && startedAt.HasValue
					? $"{State} since {startedAt.Value.ToLocalTime():HH:mm:ss}"
					: State.ToString();

			LastError = State == RelayServiceState.Faulted ? _launcher.Controller.GetLastError(Mode) : null;
			HasLastError = !string.IsNullOrWhiteSpace(LastError);

			IReadOnlyList<string> errors = _launcher.Validate(Mode);
			ValidationErrors.Reset(errors);
			HasValidationErrors = errors.Count > 0;
			ValidationSummary = errors.Count == 0
				? "Pre-flight OK"
				: $"{errors.Count} issue(s) to fix before starting";

			StartCommand.NotifyCanExecuteChanged();
			StopCommand.NotifyCanExecuteChanged();
			RestartCommand.NotifyCanExecuteChanged();
		}

		private bool CanStart() => !IsBusy && !IsRunning && !HasValidationErrors;
		private bool CanStop() => !IsBusy && (IsRunning || State == RelayServiceState.Faulted);
		private bool CanRestart() => !IsBusy && (IsRunning || State == RelayServiceState.Faulted) && !HasValidationErrors;

		[RelayCommand(CanExecute = nameof(CanStart))]
		private Task Start() => RunAsync(() => _launcher.StartAsync(Mode), "started");

		[RelayCommand(CanExecute = nameof(CanStop))]
		private Task Stop() => RunAsync(async () => { await _launcher.StopAsync(Mode).ConfigureAwait(true); return null; }, "stopped");

		[RelayCommand(CanExecute = nameof(CanRestart))]
		private Task Restart() => RunAsync(() => _launcher.RestartAsync(Mode), "restarted");

		private async Task RunAsync(Func<Task<string>> action, string verb)
		{
			IsBusy = true;
			Refresh();
			try
			{
				var error = await action().ConfigureAwait(true);
				_owner.ReportStatus(error ?? $"{DisplayName} {verb}.");
			}
			catch (Exception ex)
			{
				_owner.ReportStatus($"{DisplayName}: {ex.Message}");
			}
			finally
			{
				IsBusy = false;
				Refresh();
			}
		}

		private static string DescriptionFor(string mode)
		{
			switch (mode)
			{
				case "smtp": return "Receives dispatch emails from CAD/paging systems and creates Resgrid calls and messages.";
				case "audio": return "Listens to a sound card for paging tones and creates Resgrid calls (Windows).";
				case "radio": return "Two-way bridge between a cabled radio (analog or LTE/PoC donor radio) and a Resgrid PTT channel (Windows).";
				case "record": return "Records every PTT channel transmission to disk and/or S3 with a metadata log.";
				case "dispatch": return "Tones out new Resgrid calls (alert tones + spoken announcement) on a PTT channel.";
				default: return "";
			}
		}
	}

	/// <summary>An ObservableCollection with an atomic <see cref="Reset"/> from a fresh sequence.</summary>
	public sealed class ObservableCollectionLite<T> : System.Collections.ObjectModel.ObservableCollection<T>
	{
		public void Reset(IEnumerable<T> items)
		{
			Clear();
			foreach (var item in items)
				Add(item);
		}
	}
}
