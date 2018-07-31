using System;
using System.Diagnostics;

namespace Resgrid.Audio.Core.Events
{
	public class CallCreatedEventArgs
	{
		[DebuggerStepThrough]
		public CallCreatedEventArgs(string watcherName, int callId, string callNumber, DateTime timestamp)
		{
			WatcherName = watcherName;
			CallId = callId;
			CallNumber = callNumber;
			Timestamp = timestamp;
		}

		public string WatcherName { get; set; }
		public int CallId { get; private set; }
		public string CallNumber { get; private set; }
		public DateTime Timestamp { get; private set; }
	}
}
