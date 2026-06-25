using System;
using Serilog.Core;
using Serilog.Events;

namespace Resgrid.Relay.Engine.Logging
{
	/// <summary>
	/// Serilog sink that renders each event and forwards it onto a <see cref="UiLogBus"/>
	/// for display in the desktop UI's Logs screen.
	/// </summary>
	public sealed class UiLogSink : ILogEventSink
	{
		private readonly UiLogBus _bus;

		public UiLogSink(UiLogBus bus)
		{
			_bus = bus ?? throw new ArgumentNullException(nameof(bus));
		}

		public void Emit(LogEvent logEvent)
		{
			if (logEvent == null)
				return;

			var record = new LogRecord(
				logEvent.Timestamp,
				logEvent.Level,
				logEvent.RenderMessage(),
				logEvent.Exception?.ToString());

			_bus.Publish(record);
		}
	}
}
