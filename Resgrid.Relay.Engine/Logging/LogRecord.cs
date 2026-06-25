using System;
using Serilog.Events;

namespace Resgrid.Relay.Engine.Logging
{
	/// <summary>
	/// A single rendered log entry surfaced to the desktop UI's Logs screen.
	/// </summary>
	public readonly record struct LogRecord(DateTimeOffset Timestamp, LogEventLevel Level, string Message, string Exception);
}
