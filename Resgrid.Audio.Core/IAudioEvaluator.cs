using System.Collections.Generic;
using NAudio.Wave;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public interface IAudioEvaluator
	{
		void Init(List<Watcher> watchers);
		void Start(IWaveIn waveIn);
	}
}
