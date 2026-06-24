using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Persists a recorded transmission's audio bytes. Implementations target local
	/// disk or S3-compatible object storage; a recorder may write to several.
	/// </summary>
	public interface ITransmissionStore
	{
		/// <summary>Short identifier of the backing store ("local", "s3").</summary>
		string Kind { get; }

		/// <summary>
		/// Persists <paramref name="data"/> under a name derived from
		/// <paramref name="objectName"/> and returns where it landed.
		/// </summary>
		Task<StoredLocation> SaveAsync(string objectName, byte[] data, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Appends transmission metadata to a durable log for compliance reporting.
	/// </summary>
	public interface ITransmissionLog : System.IAsyncDisposable
	{
		Task AppendAsync(TransmissionRecord record, CancellationToken cancellationToken = default);
	}
}
