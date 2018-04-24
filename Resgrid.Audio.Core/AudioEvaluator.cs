using Resgrid.Audio.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Resgrid.Audio.Core
{
	public class AudioEvaluator: IAudioEvaluator
	{
		public bool EvaluateAudioTrigger(Trigger trigger, byte[] audio)
		{
			List<bool> validation1 = new List<bool>();
			List<bool> validation2 = new List<bool>();
			double tolerance1 = (double)(trigger.Frequency1 * 100) / trigger.Tolerance;
			double tolerance2 = (double)(trigger.Frequency2 * 100) / trigger.Tolerance;

			foreach (var a in audio)
			{
				if (trigger.Count >= 1)
				{
					if (a >= (a - tolerance1) && a <= (a + tolerance1))
						validation1.Add(true);
					else
						validation1.Add(false);
				}
				else if (trigger.Count == 2)
				{
					if (a >= (a - tolerance2) && a <= (a + tolerance2))
						validation2.Add(true);
					else
						validation2.Add(false);
				}
			}

			double arrayCount = Math.Round(80000 * trigger.Time, 0, MidpointRounding.AwayFromZero);

			switch (trigger.Count)
			{
				case 1:
					var count1 = validation1.Count(x => x);

					if (count1 >= arrayCount)
						return true;
					break;
				case 2:
					var count2_1 = validation1.Count(x => x);
					var count2_2 = validation2.Count(x => x);

					if (count2_1 >= arrayCount && count2_2 >= arrayCount)
						return true;
					break;
				default:
					break;
			}

			return false;
		}
	}
}
