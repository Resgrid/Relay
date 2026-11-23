using System;
using System.Threading;
using NAudio.Wave;
using Resgrid.Audio.Voice.Abstractions;
using Serilog;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>
	/// NAudio-backed radio device: captures receive audio with <see cref="WaveInEvent"/>
	/// and plays transmit audio with <see cref="WaveOutEvent"/>, both at 48 kHz mono
	/// PCM16 to match the LiveKit/Opus pipeline (no resampling on the device edge).
	/// </summary>
	public sealed class NAudioRadioDevice : IRadioDevice
	{
		// Output buffering. NAudio's default (300 ms over 2 buffers) played up to 300 ms of audio after the bridge saw its queue
		// empty, so the courtesy beep and the end of the transmit test's tone went out after PTT had already dropped.
		private const int OutputLatencyMs = 100;
		private const int OutputBufferCount = 3;

		// WaveIn hands over a buffer every 20 ms. Some USB removals just stop the callbacks without an error, which left the
		// bridge deaf while it reported itself healthy; none for this long is treated as the device having gone away.
		private static readonly TimeSpan ReceiveStallTimeout = TimeSpan.FromSeconds(5);

		private readonly int _inputDevice;
		private readonly int _outputDevice;
		private readonly ILogger _logger;
		private readonly WaveFormat _format = new WaveFormat(AudioFormat.SampleRate, 16, AudioFormat.Channels);

		private volatile WaveInEvent _waveIn;
		private volatile WaveOutEvent _waveOut;
		private volatile BufferedWaveProvider _txBuffer;
		private volatile PlaybackCounter _txPlayed;
		private Timer _receiveWatchdog;
		private long _lastReceiveTicks;
		private int _faultRaised;

		public event EventHandler<short[]> SamplesReceived;
		public event EventHandler<RadioFaultEventArgs> Faulted;

		public NAudioRadioDevice(int inputDevice, int outputDevice, ILogger logger)
		{
			_inputDevice = inputDevice;
			_outputDevice = outputDevice;
			_logger = logger;
		}

		public TimeSpan BufferedTransmit => _txBuffer?.BufferedDuration ?? TimeSpan.Zero;

		public TimeSpan PlayedTransmit => _txPlayed?.Played ?? TimeSpan.Zero;

		public TimeSpan OutputLatency => TimeSpan.FromMilliseconds(OutputLatencyMs);

		public void StartReceive()
		{
			if (_waveIn != null)
				return;

			var waveIn = new WaveInEvent
			{
				DeviceNumber = _inputDevice,
				WaveFormat = _format,
				BufferMilliseconds = 20
			};
			waveIn.DataAvailable += OnDataAvailable;
			waveIn.RecordingStopped += OnRecordingStopped;
			Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);
			_waveIn = waveIn;
			try
			{
				waveIn.StartRecording();
			}
			catch
			{
				// Leave nothing half-open (the device handle, a published _waveIn that blocks the next StartReceive).
				_waveIn = null;
				waveIn.DataAvailable -= OnDataAvailable;
				waveIn.RecordingStopped -= OnRecordingStopped;
				try { waveIn.Dispose(); } catch { /* ignore */ }
				throw;
			}

			_receiveWatchdog = new Timer(_ => CheckReceiveStall(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			_logger?.Information("Radio receive started on input device {Device} @ {Rate} Hz", _inputDevice, AudioFormat.SampleRate);
		}

		private void OnDataAvailable(object sender, WaveInEventArgs e)
		{
			Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);

			int sampleCount = e.BytesRecorded / 2;
			if (sampleCount <= 0)
				return;

			var samples = new short[sampleCount];
			Buffer.BlockCopy(e.Buffer, 0, samples, 0, sampleCount * 2);
			SamplesReceived?.Invoke(this, samples);
		}

		private void OnRecordingStopped(object sender, StoppedEventArgs e)
		{
			// StopReceive clears _waveIn before stopping, so only a stop nobody asked for gets here.
			if (sender == null || !ReferenceEquals(sender, _waveIn))
				return;
			RaiseFault($"Radio receive audio (input device {_inputDevice}) stopped unexpectedly", e?.Exception);
		}

		private void CheckReceiveStall()
		{
			if (_waveIn == null)
				return;

			var silentFor = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastReceiveTicks));
			if (silentFor >= ReceiveStallTimeout)
				RaiseFault($"Radio receive audio (input device {_inputDevice}) delivered nothing for {silentFor.TotalSeconds:0} s");
		}

		public void StopReceive()
		{
			var waveIn = _waveIn;
			if (waveIn == null)
				return;
			_waveIn = null;

			try { _receiveWatchdog?.Dispose(); } catch { /* ignore */ }
			_receiveWatchdog = null;

			try { waveIn.StopRecording(); } catch { /* ignore */ }
			waveIn.DataAvailable -= OnDataAvailable;
			waveIn.RecordingStopped -= OnRecordingStopped;
			waveIn.Dispose();
		}

		public void StartTransmit()
		{
			if (_waveOut != null)
				return;

			var buffer = new BufferedWaveProvider(_format)
			{
				DiscardOnBufferOverflow = true,
				BufferDuration = TimeSpan.FromSeconds(5)
			};
			var played = new PlaybackCounter(buffer);
			var waveOut = new WaveOutEvent
			{
				DeviceNumber = _outputDevice,
				DesiredLatency = OutputLatencyMs,
				NumberOfBuffers = OutputBufferCount
			};
			try
			{
				waveOut.Init(played);
				waveOut.PlaybackStopped += OnPlaybackStopped;
				_txBuffer = buffer;
				_txPlayed = played;
				_waveOut = waveOut;
				waveOut.Play();
			}
			catch
			{
				// As in StartReceive: a failed open leaves nothing behind, so the next StartTransmit can try again.
				_waveOut = null;
				_txBuffer = null;
				_txPlayed = null;
				waveOut.PlaybackStopped -= OnPlaybackStopped;
				try { waveOut.Dispose(); } catch { /* ignore */ }
				throw;
			}
			_logger?.Information("Radio transmit path opened on output device {Device} ({Latency} ms output latency)", _outputDevice, OutputLatencyMs);
		}

		private void OnPlaybackStopped(object sender, StoppedEventArgs e)
		{
			// The buffered provider never runs dry, so playback only stops when StopTransmit asks (it clears _waveOut first)
			// or the device fails. A device that just stops completing buffers (some USB stalls) never gets here: its
			// PlayedTransmit stops advancing (and its backlog stops shrinking) instead, which RadioBridge detects and faults on.
			if (sender == null || !ReferenceEquals(sender, _waveOut))
				return;
			RaiseFault($"Radio transmit audio (output device {_outputDevice}) stopped unexpectedly", e?.Exception);
		}

		public void Transmit(short[] pcm48kMono)
		{
			var buffer = _txBuffer;
			if (buffer == null || pcm48kMono == null || pcm48kMono.Length == 0)
				return;

			var bytes = new byte[pcm48kMono.Length * 2];
			Buffer.BlockCopy(pcm48kMono, 0, bytes, 0, bytes.Length);
			buffer.AddSamples(bytes, 0, bytes.Length);
		}

		public void ClearTransmit() => _txBuffer?.ClearBuffer();

		public void StopTransmit()
		{
			var waveOut = _waveOut;
			if (waveOut == null)
				return;
			_waveOut = null;
			_txBuffer = null;
			_txPlayed = null;

			try { waveOut.Stop(); } catch { /* ignore */ }
			waveOut.PlaybackStopped -= OnPlaybackStopped;
			waveOut.Dispose();
		}

		private void RaiseFault(string reason, Exception exception = null)
		{
			// One report per device: the owner tears it down and opens a new one.
			if (Interlocked.Exchange(ref _faultRaised, 1) == 1)
				return;

			_logger?.Error(exception, "{Reason}", reason);
			try
			{
				Faulted?.Invoke(this, new RadioFaultEventArgs(reason, exception));
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Radio device fault subscriber failed");
			}
		}

		public void Dispose()
		{
			StopReceive();
			StopTransmit();
		}

		// Counts what the output device pulls for playback. WaveOutEvent reads only as its buffers complete, so the count
		// stops when the device stops playing, whether or not the queue behind it is being refilled (the buffered provider
		// pads an empty queue with silence, so a healthy idle output keeps counting).
		private sealed class PlaybackCounter : IWaveProvider
		{
			private readonly IWaveProvider _source;
			private long _bytesRead;

			public PlaybackCounter(IWaveProvider source) => _source = source;

			public WaveFormat WaveFormat => _source.WaveFormat;

			public TimeSpan Played
			{
				get
				{
					// Split so a relay that runs for months does not overflow bytes * TicksPerSecond.
					long bytes = Interlocked.Read(ref _bytesRead);
					long perSecond = Math.Max(1, WaveFormat.AverageBytesPerSecond);
					return TimeSpan.FromTicks(bytes / perSecond * TimeSpan.TicksPerSecond + bytes % perSecond * TimeSpan.TicksPerSecond / perSecond);
				}
			}

			public int Read(byte[] buffer, int offset, int count)
			{
				var read = _source.Read(buffer, offset, count);
				if (read > 0)
					Interlocked.Add(ref _bytesRead, read);
				return read;
			}
		}
	}
}
