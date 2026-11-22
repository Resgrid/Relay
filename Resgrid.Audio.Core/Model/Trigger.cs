using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DtmfDetection;
using DtmfDetection.NAudio;
using System.Text.Json.Serialization;

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

		/// <summary>
		/// The detector registers whole-hertz tones (DtmfTone.HighTone is an int), so a configured frequency is rounded the same way
		/// both when it is registered and when detected tones are compared against it. Comparing the raw double meant any
		/// fractional frequency (Motorola Quick Call II tones such as 368.5 Hz) could never match.
		/// </summary>
		public static int ToneKey(double frequency) => (int)Math.Round(frequency, MidpointRounding.AwayFromZero);

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


				var tone1 = ToneKey(Frequency1);
				var tone2 = ToneKey(Frequency2);

				if (Count == 1)
				{
					var singleTone = tones.FirstOrDefault(x => x.DtmfTone.HighTone == tone1 && x.Duration >= TimeSpan.FromMilliseconds(Time1));
					return singleTone == null ? null : new List<DtmfToneEnd>() { singleTone };
				}

				if (Count == 2)
				{
					// Newest first: a stray first tone from minutes ago (one whose second tone never came) stays in the bank until it
					// is cleaned up, and pairing only the oldest candidate would make the real page that follows it miss.
					var firstTones = tones
						.Where(x => x.DtmfTone.HighTone == tone1 && x.Duration >= TimeSpan.FromMilliseconds(Time1))
						.OrderByDescending(x => x.TimeStamp)
						.ToList();

					foreach (var firstTone in firstTones)
					{
						// A different occurrence: when both frequencies round to the same whole-hertz tone (368.5 and 369.4 Hz),
						// one long tone otherwise filled both slots and paged on its own.
						var secondTone = tones.FirstOrDefault(x => !ReferenceEquals(x, firstTone) &&
							x.DtmfTone.HighTone == tone2 && x.Duration >= TimeSpan.FromMilliseconds(Time2) &&
							x.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds >= 0 &&
							x.TimeStamp.Subtract(firstTone.TimeStamp).TotalMilliseconds <= ((Time1 + Time2) * 6));

						if (secondTone != null)
							return new List<DtmfToneEnd>() { firstTone, secondTone };
					}
				}
			}

			return null;
		}
	}
}
