using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.Recording;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class TransmissionRecordTests
	{
		[Test]
		public void Serializes_WithCamelCaseComplianceFields()
		{
			var record = new TransmissionRecord
			{
				Id = "abc",
				ChannelId = "chan-1",
				ParticipantIdentity = "user-42",
				ParticipantName = "Engine 5",
				StartUtc = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc),
				EndUtc = new DateTime(2026, 6, 23, 12, 0, 5, DateTimeKind.Utc),
				DurationMs = 5000,
				Codec = "pcm_s16le"
			};
			record.Locations.Add(new StoredLocation { Kind = "local", Location = "/tmp/x.wav", SizeBytes = 10 });

			var json = JsonSerializer.Serialize(record);

			json.Should().Contain("\"participantIdentity\":\"user-42\"");
			json.Should().Contain("\"durationMs\":5000");
			json.Should().Contain("\"locations\"");
			json.Should().Contain("\"kind\":\"local\"");
		}
	}

	[TestFixture]
	public class JsonlTransmissionLogTests
	{
		[Test]
		public async Task Append_WritesOneJsonLinePerRecord()
		{
			var path = Path.Combine(Path.GetTempPath(), $"tx-{Guid.NewGuid():N}.jsonl");
			try
			{
				var log = new JsonlTransmissionLog(path);
				await log.AppendAsync(new TransmissionRecord { Id = "1", ChannelName = "Main", DurationMs = 100 });
				await log.AppendAsync(new TransmissionRecord { Id = "2", ChannelName = "Main", DurationMs = 200 });
				await log.DisposeAsync();

				var lines = File.ReadAllLines(path);
				lines.Should().HaveCount(2);

				var first = JsonSerializer.Deserialize<TransmissionRecord>(lines[0]);
				first.Id.Should().Be("1");
				first.DurationMs.Should().Be(100);
			}
			finally
			{
				if (File.Exists(path))
					File.Delete(path);
			}
		}
	}

	[TestFixture]
	public class LocalFileTransmissionStoreTests
	{
		[Test]
		public async Task Save_WritesDatePartitionedFile()
		{
			var root = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}");
			try
			{
				var store = new LocalFileTransmissionStore(root);
				var data = new byte[] { 1, 2, 3, 4 };
				var location = await store.SaveAsync("test.wav", data);

				location.Kind.Should().Be("local");
				location.SizeBytes.Should().Be(4);
				File.Exists(location.Location).Should().BeTrue();
				File.ReadAllBytes(location.Location).Should().Equal(data);
			}
			finally
			{
				if (Directory.Exists(root))
					Directory.Delete(root, recursive: true);
			}
		}
	}
}
