using System;
using System.Collections.Generic;
using System.Diagnostics;
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
		public int Time1 { get; set; }
		public int Time2 { get; set; }
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
				/*
				for (int i = 0; i < tones.Count; i++)
				{
					if (Count == 1)
					{
						if (tones[i].DtmfTone.HighTone == Frequency1 && tones[i].Duration >= new TimeSpan(0, 0, 0, 0, Time1))
							return new List<DtmfToneEnd>() { tones[i] };
					}
					else if (Count == 2)
					{
						if (tones[i].DtmfTone.HighTone == Frequency1 && tones[i].Duration >= new TimeSpan(0, 0, 0, 0, Time1))
						{
							if (i + 1 < tones.Count)
							{
								if (tones[i + 1].DtmfTone.HighTone == Frequency2 && tones[i + 1].Duration >= new TimeSpan(0, 0, 0, 0, Time1))
								{
									return new List<DtmfToneEnd>() { tones[i], tones[i + 1] };
								}
							}
						}
					}
				}
				*/


				var firstTone = tones.FirstOrDefault(x => x.DtmfTone.HighTone == Frequency1 && x.Duration >= new TimeSpan(0, 0, 0, 0, Time1));

				if (firstTone == null)
					return null;

				if (Count == 1)
				{
					return new List<DtmfToneEnd>() { firstTone };
				}
				else if (Count == 2)
				{
					var secondTonesList = tones.Where(x => x.DtmfTone.HighTone == Frequency2 && x.Duration >= new TimeSpan(0, 0, 0, 0, Time2) &&
					                                       x.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds >= 0).ToList();

					if (secondTonesList != null && secondTonesList.Count > 0)
					{
						Debugger.Log(0, "Tones", "" + Environment.NewLine);
						Debugger.Log(0, "Tones", "" + Environment.NewLine);
						Debugger.Log(0, "Tones",
							$"{firstTone.DtmfTone.HighTone} {firstTone.ToString()} at time {firstTone.TimeStamp}" + Environment.NewLine);
						Debugger.Log(0, "Tones",
							$"------------------------------------------------------------------------------------------------------------------" +
							Environment.NewLine);
						foreach (var tone in secondTonesList)
						{
							Debugger.Log(0, "Tones",
								$"{firstTone.DtmfTone.HighTone}/{tone.DtmfTone.HighTone}: {tone.ToString()} at time {tone.TimeStamp} difference to first tone {tone.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds}" +
								Environment.NewLine);
						}
					}

					var secondTone = secondTonesList.FirstOrDefault(x => x.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds <= ((Time1 + Time2) * 6));
					//var secondTone = secondTonesList.FirstOrDefault(x => x.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds <= (1000 * 60));

					if (secondTone != null)
						return new List<DtmfToneEnd>() { firstTone, secondTone };
				}
			}

			return null;
		}
	}
}
