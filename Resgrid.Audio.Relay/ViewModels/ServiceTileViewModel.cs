using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Wraps a single running <see cref="IRelayService"/> for the Dashboard. Surfaces the service's
	/// <see cref="IRelayStatus"/> (INPC, so pills, the level meter and counters bind straight through), the live lifecycle
	/// <see cref="State"/>, when it started, its last fault, and Stop/Restart.
	/// </summary>
	public partial class ServiceTileViewModel : ObservableObject, IDisposable
	{
		private readonly IRelayService _service;
		private readonly RelayModeLauncher _launcher;
		private readonly Action<string> _report;
		private bool _disposed;
		private bool _restarting;
		private bool _stopRequestedDuringRestart;

		/// <param name="report">
		/// Shows a Restart outcome on the Dashboard. The tile itself is removed as soon as its service stops, so it can't carry
		/// the message.
		/// </param>
		public ServiceTileViewModel(IRelayService service, RelayModeLauncher launcher, Action<string> report = null)
		{
			_service = service;
			_launcher = launcher;
			_report = report;
			_state = service.State;

			var started = launcher.Controller.StartedAtUtc(service.Mode);
			StartedText = started.HasValue ? $"Started {started.Value.ToLocalTime():ddd HH:mm:ss}" : "";

			_service.StateChanged += OnStateChanged;
			if (_service.Status is INotifyPropertyChanged inpc)
				inpc.PropertyChanged += OnStatusChanged;

			Refresh();
		}

		/// <summary>The underlying service mode key (e.g. "smtp", "radio").</summary>
		public string Mode => _service.Mode;

		public string DisplayName => RelayModeLauncher.FriendlyName(_service.Mode);

		/// <summary>The live health/traffic snapshot — bound directly (it is INPC).</summary>
		public IRelayStatus Status => _service.Status;

		public IRelayService Service => _service;

		/// <summary>True for modes with a live input level (radio bridge, tone-detect).</summary>
		public bool ShowAudio => _service.Mode == "radio" || _service.Mode == "audio";

		/// <summary>True for the radio bridge (squelch and TX/RX lamps).</summary>
		public bool ShowRadio => _service.Mode == "radio";

		public string StartedText { get; }

		[ObservableProperty]
		private RelayServiceState _state;

		[ObservableProperty]
		private string _lastUpdated = "—";

		[ObservableProperty]
		private string _lastError;

		[ObservableProperty]
		private bool _hasLastError;

		public void Refresh()
		{
			State = _service.State;
			LastError = State == RelayServiceState.Faulted ? _launcher.Controller.GetLastError(Mode) : null;
			HasLastError = !string.IsNullOrWhiteSpace(LastError);
		}

		[RelayCommand]
		private async Task Stop()
		{
			// During a restart the service has already left the controller (it is being torn down), so the launcher's stop finds
			// nothing to stop and the restart would then start a replacement, reversing this Stop. Record it for the restart.
			if (_restarting)
				_stopRequestedDuringRestart = true;

			await _launcher.StopAsync(Mode).ConfigureAwait(true);
		}

		[RelayCommand]
		private async Task Restart()
		{
			// Stop then start, as RelayModeLauncher.RestartAsync does, but skip the start when a Stop for this tile or a stop-all
			// (footer, tray, Operations, quit) arrived while the old instance was being torn down.
			_restarting = true;
			_stopRequestedDuringRestart = false;
			var stopAllRequests = _launcher.Controller.StopAllRequests;
			try
			{
				await _launcher.StopAsync(Mode).ConfigureAwait(true);

				if (_stopRequestedDuringRestart || _launcher.Controller.StopAllRequests != stopAllRequests)
				{
					_report?.Invoke($"{DisplayName} stopped; restart cancelled.");
					return;
				}

				var error = await _launcher.StartAsync(Mode).ConfigureAwait(true);
				_report?.Invoke(error ?? $"{DisplayName} restarted.");
			}
			catch (Exception ex)
			{
				_report?.Invoke($"{DisplayName} restart failed: {ex.Message}");
			}
			finally
			{
				_restarting = false;
			}
		}

		private void OnStateChanged(object sender, RelayStateChangedEventArgs e)
		{
			RunOnUi(() =>
			{
				Refresh();
				LastUpdated = DateTime.Now.ToString("HH:mm:ss");
			});
		}

		private void OnStatusChanged(object sender, PropertyChangedEventArgs e)
		{
			// The level meter changes ~10x a second; only the stamp needs the UI thread.
			RunOnUi(() => LastUpdated = DateTime.Now.ToString("HH:mm:ss"));
		}

		private static void RunOnUi(Action action)
		{
			var app = System.Windows.Application.Current;
			if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
				app.Dispatcher.BeginInvoke(action);
			else
				action();
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			_service.StateChanged -= OnStateChanged;
			if (_service.Status is INotifyPropertyChanged inpc)
				inpc.PropertyChanged -= OnStatusChanged;
		}
	}
}
