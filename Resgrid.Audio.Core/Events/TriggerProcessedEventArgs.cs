using System;
using System.Diagnostics;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core.Events
{
	public class TriggerProcessedEventArgs
	{
		[DebuggerStepThrough]
		public TriggerProcessedEventArgs(Watcher watcher, Trigger trigger, DateTime timestamp, byte[] mp3Audio)
		{
			Watcher = watcher;
			Trigger = trigger;
			Timestamp = timestamp;
			Mp3Audio = mp3Audio;
		}

		public Watcher Watcher { get; private set; }
		public Trigger Trigger { get; private set; }
		public DateTime Timestamp { get; private set; }
		public byte[] Mp3Audio { get; private set; }
	}
}
