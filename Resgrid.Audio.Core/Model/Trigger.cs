using System.Collections.Generic;
using System.Linq;
using DtmfDetection;
using Newtonsoft.Json;

namespace Resgrid.Audio.Core.Model
{
	public class Trigger
	{
		public double Frequency1 { get; set; }
		public double Frequency2 { get; set; }
		public int Tolerance { get; set; }
		public double Time { get; set; }
		public int Count { get; set; }

		[JsonIgnore]
		public List<DtmfTone> Tones { get; set; }

		public Trigger()
		{
			Tones = new List<DtmfTone>();
		}

		public List<DtmfTone> GetMatchingTones(List<DtmfTone> tones)
		{
			List<DtmfTone> matchingTones = new List<DtmfTone>();

			if (tones != null && tones.Any())
			{
				foreach (var tone in tones)
				{
					if (Frequency1 == tone.HighTone)
						if (!matchingTones.Contains(tone))
							matchingTones.Add(tone);

					if (Frequency2 == tone.HighTone)
						if (!matchingTones.Contains(tone))
							matchingTones.Add(tone);

					if (Count == 1)
					{
						if (matchingTones.Count() == 1)
							return matchingTones;
					}
					else if (Count == 2)
					{
						if (matchingTones.Count() == 2)
							return matchingTones;
					}
				}
			}

			return matchingTones;
		}
	}
}
