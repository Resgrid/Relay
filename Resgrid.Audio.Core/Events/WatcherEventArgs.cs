using System;
using System.Collections.Generic;
using System.Diagnostics;
using DtmfDetection;
using DtmfDetection.NAudio;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core.Events
{
	public class WatcherEventArgs
	{
		[DebuggerStepThrough]
		public WatcherEventArgs(Watcher watcher, List<Trigger> triggers, List<DtmfTone> tones, DateTime timestamp)
		{
			Watcher = watcher;
			Triggers = triggers;
			Tones = tones;
			Timestamp = timestamp;
		}

		public Watcher Watcher { get; private set; }
		public List<Trigger> Triggers { get; private set; }
		public List<DtmfTone> Tones { get; private set; }
		public DateTime Timestamp { get; private set; }
	}
}
