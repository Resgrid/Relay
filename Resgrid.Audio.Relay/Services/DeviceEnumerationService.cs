using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Resgrid.Audio.Core.Radio;
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

	/// <summary>Simple DTO describing a CM108-compatible USB HID device usable for PTT GPIO.</summary>
	public sealed class HidDeviceInfo
	{
		public int VendorId { get; set; }
		public int ProductId { get; set; }
		public string Name { get; set; }

		/// <summary>The CM108-compatible family ("C-Media CM108/CM119", "AIOC"), or null.</summary>
		public string InterfaceName => Cm108Compatibility.FindInterface(VendorId, ProductId)?.Name;

		/// <summary>
		/// How many listed interfaces have these same ids (itself included). Picking one of several identical interfaces (two
		/// AIOCs) cannot say which one: PTT refuses to key any of them, and the list says so.
		/// </summary>
		public int SameIdsCount { get; set; } = 1;

		public string Display => Cm108Compatibility.Describe(new UsbHidDevice { VendorId = VendorId, ProductId = ProductId, Name = Name }) +
			Cm108Compatibility.SameIdsNote(SameIdsCount);
	}

	/// <summary>
	/// Enumerates audio devices, serial ports and CM108-compatible HID devices for the Devices
	/// screen and the radio configuration UI. Mirrors the console's <c>devices</c> command.
	/// All methods are defensive — a failing backend yields an empty list rather than throwing.
	/// </summary>
	public sealed class DeviceEnumerationService
	{
		private readonly Func<IEnumerable<UsbHidDevice>> _hidDevices;

		public DeviceEnumerationService()
			: this(Cm108Compatibility.EnumerateHidDevices)
		{
		}

		/// <summary>Enumerates HID devices from <paramref name="hidDevices"/> (every USB HID device; tests pass a fixed list).</summary>
		public DeviceEnumerationService(Func<IEnumerable<UsbHidDevice>> hidDevices)
		{
			_hidDevices = hidDevices ?? throw new ArgumentNullException(nameof(hidDevices));
		}

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

		/// <summary>
		/// CM108-compatible USB HID devices usable for PTT GPIO: C-Media fobs (VID 0x0D8C) and the AIOC (0x1209:0x7388), per
		/// <see cref="Cm108Compatibility.KnownInterfaces"/>.
		/// </summary>
		public IReadOnlyList<HidDeviceInfo> GetCm108Devices()
		{
			try
			{
				var compatible = Cm108Compatibility.SelectCompatible(_hidDevices());
				return compatible
					.Select(d => new HidDeviceInfo
					{
						VendorId = d.VendorId,
						ProductId = d.ProductId,
						Name = d.Name,
						SameIdsCount = Cm108Compatibility.CountWithSameIds(compatible, d)
					})
					.ToList();
			}
			catch
			{
				return Array.Empty<HidDeviceInfo>();
			}
		}
	}
}
