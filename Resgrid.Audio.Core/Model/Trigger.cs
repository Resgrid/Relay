using System;
using System.Collections.Generic;
using System.Linq;
using DtmfDetection;
using DtmfDetection.NAudio;
using Newtonsoft.Json;

namespace Resgrid.Audio.Core.Model
{
	public class Trigger
	{
		public double Frequency1 { get; set; }
		public double Frequency2 { get; set; }
		public int Time { get; set; }
		public int Count { get; set; }

		[JsonIgnore]
		public List<DtmfTone> Tones { get; set; }

		public Trigger()
		{
			Tones = new List<DtmfTone>();
		}

		public List<DtmfToneEnd> GetMatchingTones(List<DtmfToneEnd> tones)
		{
			List<DtmfToneEnd> matchingTones = new List<DtmfToneEnd>();

			if (tones != null && tones.Any())
			{
				var firstTone = tones.FirstOrDefault(x => x.DtmfTone.HighTone == Frequency1 && x.Duration >= new TimeSpan(0, 0, 0, 0, Time));

				if (firstTone == null)
					return null;

				if (Count == 1)
				{
					if (matchingTones.Count() == 1)
						return new List<DtmfToneEnd>() { firstTone };
				}
				else if (Count == 2)
				{
					var secondTone = tones.FirstOrDefault(x => x.DtmfTone.HighTone == Frequency2 && x.Duration >= new TimeSpan(0, 0, 0, 0, Time) &&
					                 (x.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds <= 500 || x.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds >= -500));

					if (secondTone != null)
						return new List<DtmfToneEnd>() { firstTone, secondTone };
				}
			}

			return null;
		}
	}
}
