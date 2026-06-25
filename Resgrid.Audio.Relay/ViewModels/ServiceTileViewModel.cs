using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Wraps a single running <see cref="IRelayService"/> for the Dashboard. Surfaces the
	/// service's <see cref="IRelayStatus"/> (which is itself INPC, so connection pills, the
	/// level meter and counters bind straight through) plus a human-friendly mode name, the
	/// live lifecycle <see cref="State"/>, and a "last updated" stamp refreshed whenever the
	/// status changes.
	///
	/// NOTE: high-rate status fields (e.g. <c>InputDbfs</c>, TX/RX) propagate on every engine
	/// notification today. If that proves too chatty in the UI it can be throttled here (e.g.
	/// a coalescing timer) without touching the engine — bindings would be unaffected.
	/// </summary>
	public partial class ServiceTileViewModel : ObservableObject, IDisposable
	{
		private readonly IRelayService _service;
		private bool _disposed;

		public ServiceTileViewModel(IRelayService service)
		{
			_service = service;
			_state = service.State;

			_service.StateChanged += OnStateChanged;
			if (_service.Status is INotifyPropertyChanged inpc)
				inpc.PropertyChanged += OnStatusChanged;
		}

		/// <summary>The underlying service mode key (e.g. "smtp", "radio").</summary>
		public string Mode => _service.Mode;

		/// <summary>Title-cased mode label for the tile header.</summary>
		public string DisplayName => FriendlyMode(_service.Mode);

		/// <summary>The live health/traffic snapshot — bound directly (it is INPC).</summary>
		public IRelayStatus Status => _service.Status;

		/// <summary>The wrapped service (for Stop wiring from the dashboard, if needed).</summary>
		public IRelayService Service => _service;

		[ObservableProperty]
		private RelayServiceState _state;

		[ObservableProperty]
		private string _lastUpdated = "—";

		private void OnStateChanged(object sender, RelayStateChangedEventArgs e)
		{
			RunOnUi(() =>
			{
				State = _service.State;
				LastUpdated = DateTime.Now.ToString("HH:mm:ss");
			});
		}

		private void OnStatusChanged(object sender, PropertyChangedEventArgs e)
		{
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

		private static string FriendlyMode(string mode)
		{
			switch (mode)
			{
				case "smtp": return "SMTP Dispatch";
				case "audio": return "Audio Tone-Detect";
				case "radio": return "Radio Bridge";
				case "record": return "Recorder";
				case "dispatch": return "Dispatch Tone-Out";
				default:
					return string.IsNullOrEmpty(mode)
						? "Service"
						: char.ToUpperInvariant(mode[0]) + mode.Substring(1);
			}
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
