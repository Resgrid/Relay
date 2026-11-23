using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Recording;

namespace Resgrid.Audio.Voice.Tests
{
	/// <summary>An in-memory <see cref="IVoiceRoomSession"/> whose connection state the test drives.</summary>
	internal sealed class TestVoiceSession : IVoiceRoomSession
	{
		public string ChannelId => "channel-1";
		public string ChannelName => "Test";
		public bool IsConnected { get; set; } = true;

		public event EventHandler<VoiceAudioFrame> AudioFrameReceived;
		public event EventHandler<VoiceConnectionStateChange> ConnectionChanged;

		public int ConnectionChangedSubscribers => ConnectionChanged?.GetInvocationList().Length ?? 0;

		public void RaiseAudio(VoiceAudioFrame frame) => AudioFrameReceived?.Invoke(this, frame);

		public void RaiseConnection(bool connected, string reason)
		{
			IsConnected = connected;
			ConnectionChanged?.Invoke(this, new VoiceConnectionStateChange(connected, reason));
		}

		public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task<IAudioPublisher> CreatePublisherAsync(string trackName, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	/// <summary>Collects appended metadata rows.</summary>
	internal sealed class CapturingTransmissionLog : ITransmissionLog
	{
		private readonly List<TransmissionRecord> _records = new List<TransmissionRecord>();

		public List<TransmissionRecord> Records
		{
			get { lock (_records) return _records.ToList(); }
		}

		public Task AppendAsync(TransmissionRecord record, CancellationToken cancellationToken = default)
		{
			lock (_records)
				_records.Add(record);
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	/// <summary>A remote store that is down: every save throws.</summary>
	internal sealed class FailingTransmissionStore : ITransmissionStore
	{
		public string Kind => "s3";

		public Task<StoredLocation> SaveAsync(string objectName, byte[] data, CancellationToken cancellationToken = default) =>
			Task.FromException<StoredLocation>(new InvalidOperationException("S3 unreachable"));
	}

	/// <summary>A remote store black-holing requests: a save never completes and ignores cancellation.</summary>
	internal sealed class HangingTransmissionStore : ITransmissionStore
	{
		public string Kind => "s3";

		public Task<StoredLocation> SaveAsync(string objectName, byte[] data, CancellationToken cancellationToken = default) =>
			new TaskCompletionSource<StoredLocation>().Task;
	}

	/// <summary>
	/// A store on an unreachable network share: the call itself blocks before it returns a task, as
	/// <see cref="LocalFileTransmissionStore"/>'s synchronous folder creation would. Dispose releases the blocked thread.
	/// </summary>
	internal sealed class BlockingTransmissionStore : ITransmissionStore, IDisposable
	{
		private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
		private int _calls;

		public string Kind => "s3";

		public int Calls => Volatile.Read(ref _calls);

		/// <summary>When set, a call that gets through (the share is back) stores the recording instead of failing.</summary>
		public bool SucceedOnceReleased { get; set; }

		public Task<StoredLocation> SaveAsync(string objectName, byte[] data, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _calls);
			_release.Wait();
			return SucceedOnceReleased
				? Task.FromResult(new StoredLocation { Kind = Kind, Location = "s3://bucket/" + objectName, SizeBytes = data.LongLength })
				: Task.FromException<StoredLocation>(new System.IO.IOException("The network path was not found."));
		}

		/// <summary>The share comes back: blocked calls return, and later ones no longer block.</summary>
		public void Release() => _release.Set();

		public void Dispose() => _release.Set();
	}

	/// <summary>A store that always takes the recording, in memory.</summary>
	internal sealed class MemoryTransmissionStore : ITransmissionStore
	{
		public string Kind => "memory";

		public Task<StoredLocation> SaveAsync(string objectName, byte[] data, CancellationToken cancellationToken = default) =>
			Task.FromResult(new StoredLocation { Kind = Kind, Location = "memory://" + objectName, SizeBytes = data.LongLength });
	}

	internal static class TestAudio
	{
		public static short[] Tone(int ms, double frequency = 1000, double amplitude = 8000)
		{
			var pcm = new short[ms * AudioFormat.SampleRate / 1000];
			for (int i = 0; i < pcm.Length; i++)
				pcm[i] = (short)(Math.Sin(2 * Math.PI * frequency * i / AudioFormat.SampleRate) * amplitude);
			return pcm;
		}
	}
}
