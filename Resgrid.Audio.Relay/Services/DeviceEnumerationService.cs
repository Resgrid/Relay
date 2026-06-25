using System;
using System.Collections.Generic;
using System.Linq;
using HidSharp;
using NAudio.Wave;
using SerialPort = System.IO.Ports.SerialPort;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>Simple DTO describing an audio capture/render device.</summary>
	public sealed class AudioDeviceInfo
	{
		public int Id { get; set; }
		public string Name { get; set; }
		public int Channels { get; set; }
		public bool IsInput { get; set; }

		public string Display => $"{Id}: {Name} ({Channels} ch)";
	}

	/// <summary>Simple DTO describing a serial port available for PTT / carrier detect.</summary>
	public sealed class SerialPortInfo
	{
		public string PortName { get; set; }
	}

	/// <summary>Simple DTO describing a CM108-class USB HID device usable for PTT GPIO.</summary>
	public sealed class HidDeviceInfo
	{
		public int VendorId { get; set; }
		public int ProductId { get; set; }
		public string Name { get; set; }

		public string Display => $"VID=0x{VendorId:X4} PID=0x{ProductId:X4} {Name}";
	}

	/// <summary>
	/// Enumerates audio devices, serial ports and CM108-class HID devices for the Devices
	/// screen and the radio configuration UI. Mirrors the console's <c>devices</c> command.
	/// All methods are defensive — a failing backend yields an empty list rather than throwing.
	/// </summary>
	public sealed class DeviceEnumerationService
	{
		/// <summary>NAudio input (radio receive) devices.</summary>
		public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
		{
			var devices = new List<AudioDeviceInfo>();
			try
			{
				for (var i = 0; i < WaveIn.DeviceCount; i++)
				{
					var caps = WaveIn.GetCapabilities(i);
					devices.Add(new AudioDeviceInfo
					{
						Id = i,
						Name = caps.ProductName,
						Channels = caps.Channels,
						IsInput = true
					});
				}
			}
			catch
			{
				// Audio backend unavailable (e.g. running on a build without WPF runtime).
			}

			return devices;
		}

		/// <summary>NAudio output (radio transmit) devices. Includes the -1 "system default" entry.</summary>
		public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
		{
			var devices = new List<AudioDeviceInfo>
			{
				new AudioDeviceInfo { Id = -1, Name = "System default", Channels = 0, IsInput = false }
			};

			try
			{
				for (var i = 0; i < WaveOut.DeviceCount; i++)
				{
					var caps = WaveOut.GetCapabilities(i);
					devices.Add(new AudioDeviceInfo
					{
						Id = i,
						Name = caps.ProductName,
						Channels = caps.Channels,
						IsInput = false
					});
				}
			}
			catch
			{
			}

			return devices;
		}

		/// <summary>Serial ports for PTT keying / carrier detect.</summary>
		public IReadOnlyList<SerialPortInfo> GetSerialPorts()
		{
			try
			{
				return SerialPort.GetPortNames()
					.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
					.Select(p => new SerialPortInfo { PortName = p })
					.ToList();
			}
			catch
			{
				return Array.Empty<SerialPortInfo>();
			}
		}

		/// <summary>CM108-class USB HID devices (VID 0x0D8C) usable for PTT GPIO.</summary>
		public IReadOnlyList<HidDeviceInfo> GetCm108Devices()
		{
			try
			{
				return DeviceList.Local.GetHidDevices(0x0D8C, null)
					.Select(d => new HidDeviceInfo
					{
						VendorId = d.VendorID,
						ProductId = d.ProductID,
						Name = SafeName(d)
					})
					.ToList();
			}
			catch
			{
				return Array.Empty<HidDeviceInfo>();
			}
		}

		private static string SafeName(HidDevice device)
		{
			try { return device.GetFriendlyName(); }
			catch { return "(unnamed)"; }
		}
	}
}
