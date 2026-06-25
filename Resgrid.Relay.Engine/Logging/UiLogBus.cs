using System.Threading.Channels;

namespace Resgrid.Relay.Engine.Logging
{
	/// <summary>
	/// A bounded, lossy fan-out of <see cref="LogRecord"/>s from the Serilog pipeline to the
	/// desktop UI. Backed by a 2000-entry channel that drops the oldest entries under
	/// pressure so a busy logger never blocks the engine or grows memory unbounded.
	/// </summary>
	public sealed class UiLogBus
	{
		private readonly Channel<LogRecord> _channel;

		public UiLogBus()
		{
			_channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(2000)
			{
				FullMode = BoundedChannelFullMode.DropOldest,
				SingleReader = false,
				SingleWriter = false
			});
		}

		/// <summary>Reader the UI drains (e.g. via <c>await foreach</c>) to display log lines.</summary>
		public ChannelReader<LogRecord> Reader => _channel.Reader;

		/// <summary>Non-blocking publish; silently drops if the (bounded) channel rejects the write.</summary>
		public void Publish(LogRecord record)
		{
			_channel.Writer.TryWrite(record);
		}
	}
}
