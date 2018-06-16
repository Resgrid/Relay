using Consolas.Core;
using NAudio.Wave;
using Resgrid.Audio.Relay.Console.Args;
using Resgrid.Audio.Relay.Console.Models;

namespace Resgrid.Audio.Relay.Console.Commands
{
	public class DevicesCommand : Command
	{
		public object Execute(DevicesArgs args)
		{
			var model = new DevicesViewModel();

			int waveInDevices = WaveIn.DeviceCount;
			for (int waveInDevice = 0; waveInDevice < waveInDevices; waveInDevice++)
			{
				WaveInCapabilities deviceInfo = WaveIn.GetCapabilities(waveInDevice);
				model.Devices.Add($"Device {waveInDevice}: {deviceInfo.ProductName}, {deviceInfo.Channels} channels");
			}

			return View("Devices", model);
		}
	}
}
