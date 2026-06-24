using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Writes recorded transmissions to the local filesystem, partitioned by UTC date
	/// (root/yyyy/MM/dd/objectName) so long-running recorders don't pile thousands of
	/// files into one directory.
	/// </summary>
	public sealed class LocalFileTransmissionStore : ITransmissionStore
	{
		private readonly string _root;

		public LocalFileTransmissionStore(string root)
		{
			_root = string.IsNullOrWhiteSpace(root)
				? Path.Combine(AppContext.BaseDirectory, "recordings")
				: root;
		}

		public string Kind => "local";

		public async Task<StoredLocation> SaveAsync(string objectName, byte[] data, CancellationToken cancellationToken = default)
		{
			var now = DateTime.UtcNow;
			var dir = Path.Combine(_root, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
			Directory.CreateDirectory(dir);

			var path = Path.Combine(dir, objectName);
			await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);

			return new StoredLocation { Kind = Kind, Location = path, SizeBytes = data.LongLength };
		}
	}
}
