using System.Collections.Generic;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public interface IAudioEvaluator
	{
		bool EvaluateAudioTrigger(Trigger trigger, double[] audio);
	}
}
