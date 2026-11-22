using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.ToneOut;
using Resgrid.Providers.ApiClient.V4.Models;
using Resgrid.Relay.Engine.Voice;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class DispatchPromptTests
	{
		[Test]
		public void Prompt_Uses_Cores_Wording_So_Its_Cached_Tts_Audio_Is_Reused()
		{
			var call = new CallResultData
			{
				Name = "Structure Fire",
				Nature = "<b>Smoke</b> showing",
				Address = "123 Main St, Springfield, WA 98111, USA",
				Priority = 2,
				AlarmLevel = 1
			};

			VoiceModeRuntime.FormatCallAnnouncement(call)
				.Should().Be("New call, Structure Fire. Nature, Smoke showing. Address, 123 Main St, Springfield. Priority, High.");
		}

		[Test]
		public void Prompt_Speaks_A_Custom_Priority_By_Name_And_An_Escalation_As_An_Alarm()
		{
			var call = new CallResultData { Name = "Brush Fire", Nature = "Wind driven", Address = "Bottom of Bucks Canyon", Priority = 1043, AlarmLevel = 2 };

			VoiceModeRuntime.FormatCallAnnouncement(call, new Dictionary<int, string> { [1043] = "Code 3" })
				.Should().Be("Second alarm, Brush Fire. Nature, Wind driven. Location, Bottom of Bucks Canyon. Priority, Code 3.");
		}

		[Test]
		public void ChunkText_Keeps_Every_Chunk_Within_The_Tts_Limit()
		{
			var sentence = string.Join(" ", Enumerable.Repeat("word", 60)) + ".";
			var text = string.Join(" ", Enumerable.Repeat(sentence, 20));

			var chunks = DispatchPromptBuilder.ChunkText(text).ToList();

			chunks.Should().HaveCountGreaterThan(1);
			chunks.Should().OnlyContain(x => x.Length <= DispatchPromptBuilder.MaxChunkLength);
			string.Join(" ", chunks).Should().Be(text);
		}
	}
}
