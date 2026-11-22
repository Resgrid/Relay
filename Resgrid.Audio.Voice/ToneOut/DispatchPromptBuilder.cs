using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// Builds the spoken dispatch prompt exactly as Resgrid Core's DispatchVoicePromptBuilder does for its own voice dispatch
	/// ("New call, {name}. Nature, {nature}. Address, {address}. Priority, {priority}.") and splits it into TTS-sized chunks the
	/// same way. Matching it byte for byte matters: the TTS service caches audio by a hash of the chunk text and Core pre-warms
	/// that cache for every dispatch, so an identical prompt is served from cache instead of being synthesized on the critical
	/// path of a tone-out.
	/// </summary>
	public static class DispatchPromptBuilder
	{
		/// <summary>The TTS service's default MaxTextLength; longer text is rejected.</summary>
		public const int MaxChunkLength = 1000;

		private static readonly string[] AlarmOrdinals =
		{
			null, null, "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth"
		};

		private static readonly HashSet<string> CountryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"USA", "U.S.A.", "US", "U.S.", "United States", "United States of America", "Canada"
		};

		private static readonly HashSet<string> StateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware",
			"Florida", "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa", "Kansas", "Kentucky",
			"Louisiana", "Maine", "Maryland", "Massachusetts", "Michigan", "Minnesota", "Mississippi",
			"Missouri", "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey", "New Mexico",
			"New York", "North Carolina", "North Dakota", "Ohio", "Oklahoma", "Oregon", "Pennsylvania",
			"Rhode Island", "South Carolina", "South Dakota", "Tennessee", "Texas", "Utah", "Vermont",
			"Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming", "District of Columbia",
			"Puerto Rico", "Guam",
			"Alberta", "British Columbia", "Manitoba", "New Brunswick", "Newfoundland and Labrador",
			"Nova Scotia", "Ontario", "Prince Edward Island", "Quebec", "Saskatchewan",
			"Northwest Territories", "Nunavut", "Yukon"
		};

		// Case-sensitive: CAD feeds emit codes upper-case, and a case-insensitive match would eat segments like "La" or "In".
		private static readonly HashSet<string> StateAbbreviations = new HashSet<string>(StringComparer.Ordinal)
		{
			"AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA",
			"KS", "KY", "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
			"NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT",
			"VA", "WA", "WV", "WI", "WY", "DC", "PR", "GU",
			"AB", "BC", "MB", "NB", "NL", "NS", "ON", "PE", "QC", "SK", "NT", "NU", "YT"
		};

		private static readonly Regex PostalCodeRegex =
			new Regex(@"^(\d{5}(-\d{4})?|[A-Za-z]\d[A-Za-z]\s?\d[A-Za-z]\d)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>The default names Resgrid speaks for priorities 0-3 when a department has no custom priorities.</summary>
		public static string DefaultPriorityText(int priority)
		{
			switch (priority)
			{
				case 0: return "Low";
				case 1: return "Medium";
				case 2: return "High";
				case 3: return "Emergency";
				default: return priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
			}
		}

		public static string BuildDispatchPrompt(string name, string nature, string address, string priorityText, int alarmLevel)
		{
			var spokenNature = StripHtmlTags(nature);
			var intro = BuildAlarmIntro(alarmLevel);
			var spokenAddress = TrimAddressForSpeech(address);

			string prompt;
			if (!String.IsNullOrWhiteSpace(spokenAddress))
			{
				var placeLabel = char.IsDigit(spokenAddress.TrimStart()[0]) ? "Address" : "Location";
				prompt = $"{intro}, {name}. Nature, {spokenNature}. {placeLabel}, {spokenAddress}. Priority, {priorityText}";
			}
			else
			{
				prompt = $"{intro}, {name}. Nature, {spokenNature}. Priority, {priorityText}";
			}

			return prompt.EndsWith(".", StringComparison.Ordinal) || prompt.EndsWith("!", StringComparison.Ordinal) || prompt.EndsWith("?", StringComparison.Ordinal)
				? prompt
				: $"{prompt}.";
		}

		/// <summary>"New call" for a first dispatch, "Second alarm" / "Third alarm" / ... after escalation.</summary>
		private static string BuildAlarmIntro(int dispatchCount)
		{
			if (dispatchCount <= 1)
				return "New call";

			return dispatchCount < AlarmOrdinals.Length
				? $"{AlarmOrdinals[dispatchCount]} alarm"
				: $"Alarm {dispatchCount}";
		}

		/// <summary>
		/// Drops trailing state, postal code and country segments ("123 Main St, Springfield, WA 98111, USA" becomes
		/// "123 Main St, Springfield"). Freeform locations pass through; at least one segment is always kept.
		/// </summary>
		public static string TrimAddressForSpeech(string address)
		{
			if (String.IsNullOrWhiteSpace(address))
				return address;

			var segments = address.Split(',')
				.Select(segment => segment.Trim())
				.Where(segment => segment.Length > 0)
				.ToList();

			while (segments.Count > 1 && IsDroppableTrailingSegment(segments[segments.Count - 1]))
				segments.RemoveAt(segments.Count - 1);

			return String.Join(", ", segments);
		}

		private static bool IsDroppableTrailingSegment(string segment)
		{
			if (CountryNames.Contains(segment) || PostalCodeRegex.IsMatch(segment))
				return true;

			var lastSpaceIndex = segment.LastIndexOf(' ');
			var head = lastSpaceIndex > 0 ? segment.Substring(0, lastSpaceIndex).Trim() : segment;
			var tail = lastSpaceIndex > 0 ? segment.Substring(lastSpaceIndex + 1) : null;

			if (tail != null && !PostalCodeRegex.IsMatch(tail))
				return StateAbbreviations.Contains(segment) || StateNames.Contains(segment);

			return StateAbbreviations.Contains(head) || StateNames.Contains(head);
		}

		/// <summary>Removes markup the same way Core's StringHelpers.StripHtmlTagsCharArray does (line breaks dropped too).</summary>
		public static string StripHtmlTags(string source)
		{
			if (String.IsNullOrEmpty(source))
				return String.Empty;

			var stripped = source.Replace("\n", "").Replace("\r", "");
			var builder = new StringBuilder(stripped.Length);
			var inside = false;

			foreach (var c in stripped)
			{
				if (c == '<')
				{
					inside = true;
					continue;
				}

				if (c == '>')
				{
					inside = false;
					continue;
				}

				if (!inside)
					builder.Append(c);
			}

			return builder.ToString();
		}

		/// <summary>Splits text into chunks the TTS service accepts, on sentence boundaries where possible.</summary>
		public static IEnumerable<string> ChunkText(string text, int maxLength = MaxChunkLength)
		{
			if (String.IsNullOrWhiteSpace(text))
				yield break;

			var normalized = Regex.Replace(text, @"\s+", " ").Trim();
			if (normalized.Length <= maxLength)
			{
				yield return normalized;
				yield break;
			}

			var sentences = Regex.Split(normalized, @"(?<=[\.\!\?])\s+").Where(sentence => !String.IsNullOrWhiteSpace(sentence));
			var builder = new StringBuilder();

			foreach (var sentence in sentences)
			{
				var trimmed = sentence.Trim();

				if (trimmed.Length > maxLength)
				{
					foreach (var fragment in ChunkLongSentence(trimmed, maxLength))
					{
						if (builder.Length > 0)
						{
							yield return builder.ToString();
							builder.Clear();
						}

						yield return fragment;
					}

					continue;
				}

				if (builder.Length == 0)
				{
					builder.Append(trimmed);
					continue;
				}

				if (builder.Length + 1 + trimmed.Length <= maxLength)
				{
					builder.Append(' ').Append(trimmed);
					continue;
				}

				yield return builder.ToString();
				builder.Clear();
				builder.Append(trimmed);
			}

			if (builder.Length > 0)
				yield return builder.ToString();
		}

		private static IEnumerable<string> ChunkLongSentence(string sentence, int maxLength)
		{
			var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var builder = new StringBuilder();

			foreach (var word in words)
			{
				if (word.Length > maxLength)
				{
					if (builder.Length > 0)
					{
						yield return builder.ToString();
						builder.Clear();
					}

					for (var index = 0; index < word.Length; index += maxLength)
						yield return word.Substring(index, Math.Min(maxLength, word.Length - index));

					continue;
				}

				if (builder.Length == 0)
				{
					builder.Append(word);
					continue;
				}

				if (builder.Length + 1 + word.Length <= maxLength)
				{
					builder.Append(' ').Append(word);
					continue;
				}

				yield return builder.ToString();
				builder.Clear();
				builder.Append(word);
			}

			if (builder.Length > 0)
				yield return builder.ToString();
		}
	}
}
