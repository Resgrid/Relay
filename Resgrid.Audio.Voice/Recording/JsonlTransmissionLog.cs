using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Append-only JSON Lines transmission log. One JSON object per line makes the log
	/// trivially greppable, streamable, and tamper-evident-friendly while remaining
	/// dependency-free. Writes are serialized so concurrent finalizers don't interleave.
	/// </summary>
	public sealed class JsonlTransmissionLog : ITransmissionLog
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			WriteIndented = false
		};

		private readonly string _path;
		private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

		public JsonlTransmissionLog(string path)
		{
			_path = string.IsNullOrWhiteSpace(path)
				? Path.Combine(AppContext.BaseDirectory, "recordings", "transmissions.jsonl")
				: path;

			var dir = Path.GetDirectoryName(_path);
			if (!string.IsNullOrWhiteSpace(dir))
				Directory.CreateDirectory(dir);
		}

		public async Task AppendAsync(TransmissionRecord record, CancellationToken cancellationToken = default)
		{
			var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
			var bytes = Encoding.UTF8.GetBytes(line);

			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
				await fs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
				await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				_gate.Release();
			}
		}

		public ValueTask DisposeAsync()
		{
			_gate.Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
