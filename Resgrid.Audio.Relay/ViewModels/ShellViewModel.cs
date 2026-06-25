using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;
using Resgrid.Relay.Engine;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// View-model for the shell window: owns the overall health "traffic-light" and the
	/// master Start/Stop area. The buttons are intentionally minimal for now — they start the
	/// single configured mode and stop everything; the richer per-mode Operations screen lands
	/// in the next UI pass.
	/// </summary>
	public partial class ShellViewModel : ObservableObject
	{
		private readonly RelayController _controller;
		private readonly ConfigurationService _configuration;

		public ShellViewModel(RelayController controller, ConfigurationService configuration)
		{
			_controller = controller;
			_configuration = configuration;
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

		/// <summary>Starts the single configured mode (placeholder master start).</summary>
		[RelayCommand]
		private void Start()
		{
			if (_controller.IsRunning)
				return;

			// TODO (next UI pass): let the operator pick which mode(s) to run; for now we start
			// the single mode from the loaded configuration.
			_controller.Start(_configuration.Current);
		}

		/// <summary>Stops every running mode.</summary>
		[RelayCommand]
		private async Task StopAsync()
		{
			await _controller.StopAllAsync().ConfigureAwait(true);
		}

		private void OnOverallStateChanged(object sender, EventArgs e)
		{
			UpdateState();
		}

		private void UpdateState()
		{
			OverallState = _controller.OverallState;
			IsRunning = _controller.IsRunning;
			StatusText = IsRunning ? OverallState.ToString() : "Stopped";
		}
	}
}
