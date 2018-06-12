using System;
using System.Collections.Generic;
using NAudio.Wave;
using Resgrid.Audio.Core.Events;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public interface IAudioEvaluator
	{
		event EventHandler<EvaluatorEventArgs> EvaluatorStarted;
		event EventHandler<EvaluatorEventArgs> EvaluatorFinished;
		event EventHandler<WatcherEventArgs> WatcherTriggered;

		void Init(List<Watcher> watchers);
		void Start(IWaveIn waveIn);
	}
}
