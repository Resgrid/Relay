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
		public void Key() => IsKeyed = true;
		public void Unkey() => IsKeyed = false;
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

		public void Key()
		{
			Set(true);
			IsKeyed = true;
		}

		public void Unkey()
		{
			Set(false);
			IsKeyed = false;
		}

		private void Set(bool on)
		{
			try
			{
				if (_useDtr)
					_port.DtrEnable = on;
				else
					_port.RtsEnable = on;
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to set serial PTT line");
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
	/// RB-USB). Writes the HID output report that drives the GPIO pin. The exact pin
	/// varies by interface, so it is configurable (URI/RIM commonly use GPIO3).
	/// </summary>
	public sealed class Cm108PttController : IPttController
	{
		private readonly HidDevice _device;
		private readonly HidStream _stream;
		private readonly int _gpioBit;
		private readonly ILogger _logger;

		public Cm108PttController(int vendorId, int productId, int gpioPin, ILogger logger)
		{
			_logger = logger;
			_gpioBit = 1 << (Math.Max(1, gpioPin) - 1);

			var devices = DeviceList.Local.GetHidDevices(vendorId, productId == 0 ? (int?)null : productId).ToList();
			_device = devices.FirstOrDefault()
				?? throw new InvalidOperationException(
					$"No CM108-class HID device found (VID=0x{vendorId:X4}, PID=0x{productId:X4}). Check the radio interface is connected.");

			if (!_device.TryOpen(out _stream))
				throw new InvalidOperationException("Failed to open the CM108 HID device for PTT control.");

			Unkey();
		}

		public bool IsKeyed { get; private set; }

		public void Key()
		{
			WriteGpio(true);
			IsKeyed = true;
		}

		public void Unkey()
		{
			WriteGpio(false);
			IsKeyed = false;
		}

		private void WriteGpio(bool on)
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
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to write CM108 GPIO PTT report");
			}
		}

		public void Dispose()
		{
			try { Unkey(); } catch { /* ignore */ }
			try { _stream?.Dispose(); } catch { /* ignore */ }
		}
	}
}
