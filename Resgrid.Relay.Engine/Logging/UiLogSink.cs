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
				RenderLiteral(logEvent),
				logEvent.Exception?.ToString());

			_bus.Publish(record);
		}

		/// <summary>
		/// Renders the message with string values as-is (Serilog's default quotes them, so "Relay mode '{Mode}'" showed as
		/// '"radio"'), matching the {Message:lj} rendering of the log files.
		/// </summary>
		private static string RenderLiteral(LogEvent logEvent)
		{
			var builder = new System.Text.StringBuilder();
			foreach (var token in logEvent.MessageTemplate.Tokens)
			{
				if (token is Serilog.Parsing.PropertyToken property
					&& logEvent.Properties.TryGetValue(property.PropertyName, out var value)
					&& value is ScalarValue { Value: string text })
				{
					builder.Append(text);
					continue;
				}

				using var writer = new System.IO.StringWriter();
				token.Render(logEvent.Properties, writer);
				builder.Append(writer.ToString());
			}
			return builder.ToString();
		}
	}
}
