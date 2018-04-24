using Resgrid.Audio.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Resgrid.Audio.Core
{
	public class AudioEvaluator: IAudioEvaluator
	{
		private const int SEC_ARRAY_COUNT = 80000;

		public bool EvaluateAudioTrigger(Trigger trigger, double[] audio)
		{
			List<bool> validation1 = new List<bool>();
			List<bool> validation2 = new List<bool>();
			double tolerance1 = 0;
			double tolerance2 = 0;

			if (trigger.Tolerance != 0)
			{
				tolerance1 = (double) (trigger.Frequency1 * trigger.Tolerance) / 100;
				tolerance2 = (double) (trigger.Frequency2 * trigger.Tolerance) / 100;
			}
			
			foreach (var a in audio)
			{
				if (trigger.Count >= 1)
				{
					if (a >= (trigger.Frequency1 - tolerance1) && a <= (trigger.Frequency1 + tolerance1))
						validation1.Add(true);
					else
						validation1.Add(false);
				}
				else if (trigger.Count == 2)
				{
					if (a >= (trigger.Frequency2 - tolerance2) && a <= (trigger.Frequency2 + tolerance2))
						validation2.Add(true);
					else
						validation2.Add(false);
				}
			}

			double arrayCount = Math.Round(SEC_ARRAY_COUNT * trigger.Time, 0, MidpointRounding.AwayFromZero);

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
