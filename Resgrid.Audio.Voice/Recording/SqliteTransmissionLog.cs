using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Queryable transmission log backed by SQLite. Useful when compliance staff need
	/// to search/report (by user, channel, time range) rather than scan a flat file.
	/// The full record is also stored as JSON for forward-compatibility.
	/// </summary>
	public sealed class SqliteTransmissionLog : ITransmissionLog
	{
		private readonly string _connectionString;
		private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

		public SqliteTransmissionLog(string databasePath)
		{
			if (string.IsNullOrWhiteSpace(databasePath))
				databasePath = Path.Combine(AppContext.BaseDirectory, "recordings", "transmissions.db");

			var dir = Path.GetDirectoryName(databasePath);
			if (!string.IsNullOrWhiteSpace(dir))
				Directory.CreateDirectory(dir);

			_connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
			EnsureSchema();
		}

		private void EnsureSchema()
		{
			using var conn = new SqliteConnection(_connectionString);
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS transmissions (
    id TEXT PRIMARY KEY,
    channel_id TEXT,
    channel_name TEXT,
    participant_identity TEXT,
    participant_name TEXT,
    track_sid TEXT,
    start_utc TEXT,
    end_utc TEXT,
    duration_ms INTEGER,
    sample_rate INTEGER,
    channels INTEGER,
    codec TEXT,
    samples INTEGER,
    locations TEXT,
    record_json TEXT
);
CREATE INDEX IF NOT EXISTS ix_tx_channel_start ON transmissions(channel_id, start_utc);
CREATE INDEX IF NOT EXISTS ix_tx_identity ON transmissions(participant_identity);";
			cmd.ExecuteNonQuery();
		}

		public async Task AppendAsync(TransmissionRecord record, CancellationToken cancellationToken = default)
		{
			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				await using var conn = new SqliteConnection(_connectionString);
				await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
				var cmd = conn.CreateCommand();
				cmd.CommandText = @"
INSERT OR REPLACE INTO transmissions
(id, channel_id, channel_name, participant_identity, participant_name, track_sid,
 start_utc, end_utc, duration_ms, sample_rate, channels, codec, samples, locations, record_json)
VALUES ($id,$cid,$cname,$pid,$pname,$tsid,$start,$end,$dur,$rate,$ch,$codec,$samples,$loc,$json);";

				cmd.Parameters.AddWithValue("$id", record.Id ?? string.Empty);
				cmd.Parameters.AddWithValue("$cid", (object)record.ChannelId ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$cname", (object)record.ChannelName ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$pid", (object)record.ParticipantIdentity ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$pname", (object)record.ParticipantName ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$tsid", (object)record.TrackSid ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$start", record.StartUtc.ToString("O"));
				cmd.Parameters.AddWithValue("$end", record.EndUtc.ToString("O"));
				cmd.Parameters.AddWithValue("$dur", record.DurationMs);
				cmd.Parameters.AddWithValue("$rate", record.SampleRate);
				cmd.Parameters.AddWithValue("$ch", record.Channels);
				cmd.Parameters.AddWithValue("$codec", (object)record.Codec ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$samples", record.Samples);
				cmd.Parameters.AddWithValue("$loc", JsonSerializer.Serialize(record.Locations));
				cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record));

				await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				_gate.Release();
			}
		}

		public ValueTask DisposeAsync()
		{
			_gate.Dispose();
			SqliteConnection.ClearAllPools();
			return ValueTask.CompletedTask;
		}
	}
}
