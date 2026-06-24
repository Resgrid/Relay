using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Resgrid.Audio.Voice.Recording
{
	/// <summary>
	/// Uploads recorded transmissions to S3 (or any S3-compatible endpoint such as
	/// MinIO, matching how Resgrid stores TTS audio). Objects are keyed by UTC date
	/// under an optional prefix.
	/// </summary>
	public sealed class S3TransmissionStore : ITransmissionStore, IDisposable
	{
		private readonly IAmazonS3 _client;
		private readonly string _bucket;
		private readonly string _prefix;
		private readonly bool _ownsClient;

		public S3TransmissionStore(IAmazonS3 client, string bucket, string prefix, bool ownsClient = false)
		{
			_client = client ?? throw new ArgumentNullException(nameof(client));
			_bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
			_prefix = (prefix ?? string.Empty).Trim('/');
			_ownsClient = ownsClient;
		}

		public string Kind => "s3";

		/// <summary>
		/// Builds a store from connection settings. Supports a custom
		/// <paramref name="endpoint"/> (S3-compatible services) or an AWS
		/// <paramref name="region"/>. Falls back to the ambient AWS credential chain
		/// when no access key is supplied.
		/// </summary>
		public static S3TransmissionStore Create(
			string endpoint, string accessKey, string secretKey, string region,
			string bucket, string prefix, bool forcePathStyle, bool useSsl)
		{
			var cfg = new AmazonS3Config();

			if (!string.IsNullOrWhiteSpace(endpoint))
			{
				if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
					endpoint = (useSsl ? "https://" : "http://") + endpoint;
				cfg.ServiceURL = endpoint;
				cfg.ForcePathStyle = forcePathStyle;
			}
			else if (!string.IsNullOrWhiteSpace(region))
			{
				cfg.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
			}

			IAmazonS3 client = !string.IsNullOrWhiteSpace(accessKey)
				? new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), cfg)
				: new AmazonS3Client(cfg);

			return new S3TransmissionStore(client, bucket, prefix, ownsClient: true);
		}

		public async Task<StoredLocation> SaveAsync(string objectName, byte[] data, CancellationToken cancellationToken = default)
		{
			var now = DateTime.UtcNow;
			var key = string.Join("/",
				string.IsNullOrEmpty(_prefix) ? null : _prefix,
				now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"), objectName)
				.TrimStart('/');

			using var stream = new MemoryStream(data, writable: false);
			var request = new PutObjectRequest
			{
				BucketName = _bucket,
				Key = key,
				InputStream = stream,
				ContentType = "audio/wav",
				AutoCloseStream = false
			};

			await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

			return new StoredLocation { Kind = Kind, Location = $"s3://{_bucket}/{key}", SizeBytes = data.LongLength };
		}

		public void Dispose()
		{
			if (_ownsClient)
				_client?.Dispose();
		}
	}
}
