using System;
using System.IO.Ports;
using System.Linq;
using HidSharp;
using Serilog;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>
	/// VOX keying: the radio interface (e.g. a SignaLink) keys from the transmit audio
	/// itself, so there is no control line. We only track logical state.
	/// </summary>
	public sealed class VoxPttController : IPttController
	{
		public bool IsKeyed { get; private set; }

		public bool Key()
		{
			IsKeyed = true;
			return true;
		}

		public bool Unkey()
		{
			IsKeyed = false;
			return true;
		}

		public void Dispose() { }
	}

	/// <summary>
	/// Keys PTT by asserting RTS or DTR on a serial / USB-serial port — the classic and
	/// most universal hardware keying method (CH340/FTDI cables, RIM, homebrew).
	/// </summary>
	public sealed class SerialPttController : IPttController, IDisposable
	{
		private readonly SerialPort _port;
		private readonly bool _useDtr;
		private readonly bool _ownsPort;
		private readonly ILogger _logger;
		private bool _failing;

		public SerialPttController(string portName, bool useDtr, ILogger logger, SerialPort sharedPort = null)
		{
			_useDtr = useDtr;
			_logger = logger;

			if (sharedPort != null)
			{
				_port = sharedPort;
				_ownsPort = false;
			}
			else
			{
				_port = new SerialPort(portName) { ReadTimeout = 200, WriteTimeout = 200 };
				_ownsPort = true;
			}

			if (!_port.IsOpen)
				_port.Open();

			Unkey();
		}

		public bool IsKeyed { get; private set; }

		public bool Key() => Set(true);

		public bool Unkey() => Set(false);

		// IsKeyed changes only when the line was actually driven: reporting a failed write as done let the bridge believe the
		// radio had unkeyed (and stop retrying) while the transmitter was still on the air.
		private bool Set(bool on)
		{
			try
			{
				// A closed port accepts RtsEnable/DtrEnable without touching the hardware.
				if (!_port.IsOpen)
					throw new InvalidOperationException($"Serial port {_port.PortName} is not open.");

				if (_useDtr)
					_port.DtrEnable = on;
				else
					_port.RtsEnable = on;

				IsKeyed = on;
				if (_failing)
				{
					_failing = false;
					_logger?.Information("Serial PTT line on {Port} is responding again", _port.PortName);
				}
				return true;
			}
			catch (Exception ex)
			{
				if (!_failing)
				{
					_failing = true;
					_logger?.Error(ex, "Failed to {Action} the serial PTT line ({Line}) on {Port}", on ? "key" : "unkey", _useDtr ? "DTR" : "RTS", _port.PortName);
				}
				else
				{
					_logger?.Debug(ex, "Serial PTT line on {Port} still failing", _port.PortName);
				}
				return false;
			}
		}

		public void Dispose()
		{
			try { Unkey(); } catch { /* ignore */ }
			if (_ownsPort)
			{
				try { _port.Dispose(); } catch { /* ignore */ }
			}
		}
	}

	/// <summary>
	/// Keys PTT via a CM108/CM119 USB sound-fob GPIO line (DMK URI, RA-40/RIM-Lite,
	/// RB-USB) or a CM108-compatible interface such as the AIOC. Writes the HID output report that drives the GPIO pin. The
	/// exact pin varies by interface, so it is configurable (URI/RIM and the AIOC commonly use GPIO3). Which device the
	/// vendor/product ids select follows <see cref="Cm108Compatibility.IsSelectedBy"/>, the rule the device pickers use; ids
	/// that select more than one connected device are refused (<see cref="Cm108Compatibility.SelectPttDevice"/>).
	/// </summary>
	public sealed class Cm108PttController : IPttController
	{
		private readonly HidDevice _device;
		private readonly HidStream _stream;
		private readonly int _gpioBit;
		private readonly ILogger _logger;
		private bool _failing;

		public Cm108PttController(int vendorId, int productId, int gpioPin, ILogger logger)
		{
			_logger = logger;
			_gpioBit = 1 << (Math.Max(1, gpioPin) - 1);

			// Exactly one device must match: two with the same ids (two AIOCs) cannot be told apart, and opening the first could
			// key a different radio, so that throws (naming the fix) as no match does.
			var devices = DeviceList.Local.GetHidDevices().ToList();
			var present = devices.Select(Cm108Compatibility.ToUsbHidDevice).ToList();
			var selected = Cm108Compatibility.SelectPttDevice(present, vendorId, productId);
			_device = devices[present.IndexOf(selected)];

			if (!_device.TryOpen(out _stream))
				throw new InvalidOperationException("Failed to open the CM108 HID device for PTT control.");

			Unkey();
		}

		public bool IsKeyed { get; private set; }

		public bool Key() => WriteGpio(true);

		public bool Unkey() => WriteGpio(false);

		private bool WriteGpio(bool on)
		{
			// CM108 HID output report: [reportId=0, 0, GPIO data, GPIO mask, 0].
			// The data bit drives the pin; the mask marks it as an output.
			var report = new byte[]
			{
				0x00,
				0x00,
				(byte)(on ? _gpioBit : 0x00),
				(byte)_gpioBit,
				0x00
			};

			try
			{
				_stream.Write(report);
				IsKeyed = on;
				if (_failing)
				{
					_failing = false;
					_logger?.Information("CM108 GPIO PTT is responding again");
				}
				return true;
			}
			catch (Exception ex)
			{
				if (!_failing)
				{
					_failing = true;
					_logger?.Error(ex, "Failed to write the CM108 GPIO PTT report ({Action})", on ? "key" : "unkey");
				}
				else
				{
					_logger?.Debug(ex, "CM108 GPIO PTT still failing");
				}
				return false;
			}
		}

		public void Dispose()
		{
			try { Unkey(); } catch { /* ignore */ }
			try { _stream?.Dispose(); } catch { /* ignore */ }
		}
	}
}
