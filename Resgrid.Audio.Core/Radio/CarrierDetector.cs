using System;
using System.IO.Ports;
using Serilog;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>
	/// Reports whether the radio currently has a carrier (squelch open), used when the
	/// squelch mode is hardware Carrier/COR/COS rather than audio VOX.
	/// </summary>
	public interface ICarrierDetector : IDisposable
	{
		bool IsCarrierPresent { get; }
	}

	/// <summary>A carrier detector that is always considered open (no hardware COR).</summary>
	public sealed class NullCarrierDetector : ICarrierDetector
	{
		public bool IsCarrierPresent => true;
		public void Dispose() { }
	}

	/// <summary>
	/// Reads carrier-detect (COR/COS) from a serial control pin (CTS or DSR). Many
	/// interface cables route the radio's COR/SQL output to one of these pins.
	/// </summary>
	public sealed class SerialCarrierDetector : ICarrierDetector
	{
		private readonly SerialPort _port;
		private readonly bool _useDsr;
		private readonly bool _inverted;
		private readonly bool _ownsPort;
		private readonly ILogger _logger;

		public SerialCarrierDetector(string portName, bool useDsr, bool inverted, ILogger logger, SerialPort sharedPort = null)
		{
			_useDsr = useDsr;
			_inverted = inverted;
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
		}

		public bool IsCarrierPresent
		{
			get
			{
				try
				{
					bool raw = _useDsr ? _port.DsrHolding : _port.CtsHolding;
					return _inverted ? !raw : raw;
				}
				catch (Exception ex)
				{
					_logger?.Debug(ex, "Failed reading serial carrier pin");
					return false;
				}
			}
		}

		public void Dispose()
		{
			if (_ownsPort)
			{
				try { _port.Dispose(); } catch { /* ignore */ }
			}
		}
	}
}
