using System;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Resgrid.Relay.Engine.Logging
{
	/// <summary>
	/// Serilog configuration helpers for wiring the <see cref="UiLogBus"/> into a logger,
	/// e.g. <c>new LoggerConfiguration().WriteTo.UiBus(bus)</c>.
	/// </summary>
	public static class UiBusSinkExtensions
	{
		/// <summary>Writes rendered log events to the supplied <see cref="UiLogBus"/>.</summary>
		public static LoggerConfiguration UiBus(
			this LoggerSinkConfiguration sinkConfig,
			UiLogBus bus,
			LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose)
		{
			if (sinkConfig == null)
				throw new ArgumentNullException(nameof(sinkConfig));
			if (bus == null)
				throw new ArgumentNullException(nameof(bus));

			return sinkConfig.Sink(new UiLogSink(bus), restrictedToMinimumLevel);
		}
	}
}
