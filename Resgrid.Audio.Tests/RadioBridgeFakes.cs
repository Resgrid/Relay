using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Core.Radio;
using Resgrid.Audio.Voice.Abstractions;

namespace Resgrid.Audio.Tests
{
	/// <summary>Test doubles shared by the <see cref="RadioBridge"/> tests.</summary>
	internal static class RadioTestAudio
	{
		public const int SamplesPerMs = AudioFormat.SampleRate / 1000;

		public static short[] Tone(int ms, double amplitude, double frequency = 1000)
		{
			var pcm = new short[ms * SamplesPerMs];
			for (int i = 0; i < pcm.Length; i++)
				pcm[i] = (short)(Math.Sin(2 * Math.PI * frequency * i / AudioFormat.SampleRate) * amplitude);
			return pcm;
		}

		public static VoiceAudioFrame AudibleFrame(string trackSid = "track-1") =>
			new VoiceAudioFrame(null, trackSid, Tone(10, 16000), DateTime.UtcNow);

		/// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeoutMs"/> passes.</summary>
		public static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 3000)
		{
			var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (!condition())
			{
				if (DateTime.UtcNow >= deadline)
					return false;
				await Task.Delay(10);
			}
			return true;
		}
	}

	internal sealed class FakeRadioDevice : IRadioDevice
	{
		private readonly object _lock = new object();
		private readonly List<short[]> _writes = new List<short[]>();

		public event EventHandler<short[]> SamplesReceived;
		public event EventHandler<RadioFaultEventArgs> Faulted;

		public List<short[]> Writes
		{
			get { lock (_lock) return _writes.ToList(); }
		}

		private TimeSpan _bufferedTransmit = TimeSpan.Zero;

		/// <summary>What the device reports as still queued (a real device's backlog the bridge cannot see).</summary>
		public TimeSpan BufferedTransmit
		{
			get => BufferedTransmitSource?.Invoke() ?? _bufferedTransmit;
			set => _bufferedTransmit = value;
		}

		/// <summary>When set, computes <see cref="BufferedTransmit"/> on every read (a backlog that changes over time).</summary>
		public Func<TimeSpan> BufferedTransmitSource { get; set; }

		public TimeSpan OutputLatency { get; set; } = TimeSpan.Zero;

		public void StartReceive() { }
		public void StopReceive() { }
		public void StartTransmit() { }
		public void StopTransmit() { }

		public void Transmit(short[] pcm48kMono)
		{
			lock (_lock)
				_writes.Add((short[])pcm48kMono.Clone());
		}

		/// <summary>Delivers a captured radio receive frame, as the capture thread would.</summary>
		public void Receive(short[] frame) => SamplesReceived?.Invoke(this, frame);

		/// <summary>Reports the device as stopped (a USB glitch / unplug).</summary>
		public void Fail(string reason) => Faulted?.Invoke(this, new RadioFaultEventArgs(reason));

		public void Dispose() { }
	}

	/// <summary>A PTT line. With <see cref="FailWrites"/> set, writes fail and the line keeps its state, like a dead serial/HID line.</summary>
	internal sealed class FakePtt : IPttController
	{
		private volatile bool _keyed;
		private int _keyCalls;
		private int _unkeyCalls;

		public bool IsKeyed => _keyed;

		public volatile bool FailWrites;

		/// <summary>How long Key() takes to return after the line is asserted (a slow HID write).</summary>
		public int KeyDelayMs { get; set; }

		public int KeyCalls => Volatile.Read(ref _keyCalls);
		public int UnkeyCalls => Volatile.Read(ref _unkeyCalls);

		public bool Key()
		{
			Interlocked.Increment(ref _keyCalls);
			if (FailWrites)
				return false;
			_keyed = true;
			if (KeyDelayMs > 0)
				Thread.Sleep(KeyDelayMs);
			return true;
		}

		public bool Unkey()
		{
			Interlocked.Increment(ref _unkeyCalls);
			if (FailWrites)
				return false;
			_keyed = false;
			return true;
		}

		public void Dispose() { }
	}

	internal sealed class FakeSession : IVoiceRoomSession
	{
		public FakePublisher Publisher { get; } = new FakePublisher();

		public string ChannelId => "channel-1";
		public string ChannelName => "Test";
		public bool IsConnected => true;

		public event EventHandler<VoiceAudioFrame> AudioFrameReceived;
		public event EventHandler<VoiceConnectionStateChange> ConnectionChanged { add { } remove { } }

		public void Raise(VoiceAudioFrame frame) => AudioFrameReceived?.Invoke(this, frame);

		public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task<IAudioPublisher> CreatePublisherAsync(string trackName, CancellationToken cancellationToken = default)
			=> Task.FromResult<IAudioPublisher>(Publisher);

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	internal sealed class FakePublisher : IAudioPublisher
	{
		private int _attempts;
		private int _written;

		/// <summary>The first this-many writes throw.</summary>
		public int FailFirst { get; set; }

		public volatile bool AlwaysFail;

		public int Attempts => Volatile.Read(ref _attempts);
		public int Written => Volatile.Read(ref _written);

		public ValueTask WriteAsync(ReadOnlyMemory<short> pcm48kMono, CancellationToken cancellationToken = default)
		{
			var attempt = Interlocked.Increment(ref _attempts);
			if (AlwaysFail || attempt <= FailFirst)
				throw new InvalidOperationException("publish failed");
			Interlocked.Increment(ref _written);
			return ValueTask.CompletedTask;
		}

		public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
