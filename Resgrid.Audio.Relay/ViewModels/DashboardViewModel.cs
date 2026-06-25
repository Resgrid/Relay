using System;
using System.Collections.Specialized;
using System.Linq;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Resgrid.Audio.Relay.Services;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// Dashboard: an overall summary plus one live tile per currently-running service. Tiles
	/// are kept in sync with <see cref="RelayController.RunningServices"/> (the controller
	/// marshals those mutations to the UI thread already), and each tile binds to its service's
	/// <see cref="IRelayStatus"/> for connection pills, the level meter, squelch / TX / RX state
	/// and the traffic counters.
	/// </summary>
	public partial class DashboardViewModel : ObservableObject, IDisposable
	{
		private readonly RelayController _controller;
		private bool _disposed;

		public DashboardViewModel(RelayController controller)
		{
			_controller = controller;

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

		/// <summary>True when nothing is running (drives the empty-state hint).</summary>
		[ObservableProperty]
		private bool _isIdle = true;

		/// <summary>One tile per running service. Bound by the Dashboard view.</summary>
		public ObservableCollection<ServiceTileViewModel> Tiles { get; } = new ObservableCollection<ServiceTileViewModel>();

		private void OnRunningServicesChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			// Simplest correct approach: rebuild the tile list to match the running set. The
			// running set is tiny (one tile per mode), so this is cheap and avoids index drift.
			RebuildTiles();
			UpdateSummary();
		}

		private void OnOverallStateChanged(object sender, EventArgs e)
		{
			UpdateSummary();
		}

		private void RebuildTiles()
		{
			foreach (var tile in Tiles)
				tile.Dispose();
			Tiles.Clear();

			foreach (var service in _controller.RunningServices)
				Tiles.Add(new ServiceTileViewModel(service));
		}

		private void UpdateSummary()
		{
			OverallState = _controller.OverallState;
			RunningCount = _controller.RunningServices.Count;
			IsIdle = RunningCount == 0;

			if (RunningCount == 0)
			{
				SummaryText = "No services running";
				return;
			}

			var modes = string.Join(", ", _controller.RunningServices.Select(s => s.Mode));
			SummaryText = RunningCount == 1
				? $"1 service running ({modes})"
				: $"{RunningCount} services running ({modes})";
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
}
