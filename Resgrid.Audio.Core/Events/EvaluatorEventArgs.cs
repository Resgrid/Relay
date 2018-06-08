using System;
using System.Diagnostics;
using DtmfDetection.NAudio;

namespace Resgrid.Audio.Core.Events
{
	public class EvaluatorEventArgs
	{
		[DebuggerStepThrough]
		public EvaluatorEventArgs(DtmfToneStart toneStart, DtmfToneEnd toneEnd, DateTime timestamp)
		{
			ToneStart = toneStart;
			ToneEnd = toneEnd;
			Timestamp = timestamp;
		}

		public DtmfToneStart ToneStart { get; private set; }
		public DtmfToneEnd ToneEnd { get; private set; }
		public DateTime Timestamp { get; private set; }

		public bool IsStartEvent
		{
			get
			{
				if (ToneStart != null)
					return true;

				return false;
			}
		}
	}
}
