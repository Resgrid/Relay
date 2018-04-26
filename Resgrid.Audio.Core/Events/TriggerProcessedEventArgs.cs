using System;
using System.Diagnostics;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core.Events
{
	public class TriggerProcessedEventArgs
	{
		[DebuggerStepThrough]
		public TriggerProcessedEventArgs(Watcher watcher, Trigger trigger, DateTime timestamp)
		{
			Watcher = watcher;
			Trigger = trigger;
			Timestamp = timestamp;
		}
		public Watcher Watcher { get; private set; }
		public Trigger Trigger { get; private set; }
		public DateTime Timestamp { get; private set; }
	}
}
