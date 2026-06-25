using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// View-model for the Devices screen. Enumerates audio input/output devices, serial ports
	/// and CM108-class HID devices via <see cref="DeviceEnumerationService"/> for the operator
	/// to reference when configuring the radio mode. The device data is Windows-only; on other
	/// hosts the service returns empty lists, so the screen simply shows the "none found" hint.
	/// </summary>
	public partial class DevicesViewModel : ObservableObject
	{
		private readonly DeviceEnumerationService _devices;

		public DevicesViewModel(DeviceEnumerationService devices)
		{
			_devices = devices;
			Refresh();
		}

		[ObservableProperty]
		private string _title = "Devices";

		[ObservableProperty]
		private IReadOnlyList<AudioDeviceInfo> _inputDevices;

		[ObservableProperty]
		private IReadOnlyList<AudioDeviceInfo> _outputDevices;

		[ObservableProperty]
		private IReadOnlyList<SerialPortInfo> _serialPorts;

		[ObservableProperty]
		private IReadOnlyList<HidDeviceInfo> _cm108Devices;

		/// <summary>When the lists were last enumerated (for an operator-visible timestamp).</summary>
		[ObservableProperty]
		private string _lastRefreshed = "";

		[RelayCommand]
		private void Refresh()
		{
			InputDevices = _devices.GetInputDevices();
			OutputDevices = _devices.GetOutputDevices();
			SerialPorts = _devices.GetSerialPorts();
			Cm108Devices = _devices.GetCm108Devices();
			LastRefreshed = $"Last refreshed {DateTime.Now:HH:mm:ss}";
		}
	}
}
