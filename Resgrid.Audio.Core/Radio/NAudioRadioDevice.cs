using System;
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
		private readonly int _inputDevice;
		private readonly int _outputDevice;
		private readonly ILogger _logger;
		private readonly WaveFormat _format = new WaveFormat(AudioFormat.SampleRate, 16, AudioFormat.Channels);

		private WaveInEvent _waveIn;
		private WaveOutEvent _waveOut;
		private BufferedWaveProvider _txBuffer;

		public event EventHandler<short[]> SamplesReceived;

		public NAudioRadioDevice(int inputDevice, int outputDevice, ILogger logger)
		{
			_inputDevice = inputDevice;
			_outputDevice = outputDevice;
			_logger = logger;
		}

		public void StartReceive()
		{
			if (_waveIn != null)
				return;

			_waveIn = new WaveInEvent
			{
				DeviceNumber = _inputDevice,
				WaveFormat = _format,
				BufferMilliseconds = 20
			};
			_waveIn.DataAvailable += OnDataAvailable;
			_waveIn.StartRecording();
			_logger?.Information("Radio receive started on input device {Device} @ {Rate} Hz", _inputDevice, AudioFormat.SampleRate);
		}

		private void OnDataAvailable(object sender, WaveInEventArgs e)
		{
			int sampleCount = e.BytesRecorded / 2;
			if (sampleCount <= 0)
				return;

			var samples = new short[sampleCount];
			Buffer.BlockCopy(e.Buffer, 0, samples, 0, sampleCount * 2);
			SamplesReceived?.Invoke(this, samples);
		}

		public void StopReceive()
		{
			if (_waveIn == null)
				return;
			try { _waveIn.StopRecording(); } catch { /* ignore */ }
			_waveIn.DataAvailable -= OnDataAvailable;
			_waveIn.Dispose();
			_waveIn = null;
		}

		public void StartTransmit()
		{
			if (_waveOut != null)
				return;

			_txBuffer = new BufferedWaveProvider(_format)
			{
				DiscardOnBufferOverflow = true,
				BufferDuration = TimeSpan.FromSeconds(5)
			};
			_waveOut = new WaveOutEvent { DeviceNumber = _outputDevice };
			_waveOut.Init(_txBuffer);
			_waveOut.Play();
			_logger?.Information("Radio transmit path opened on output device {Device}", _outputDevice);
		}

		public void Transmit(short[] pcm48kMono)
		{
			if (_txBuffer == null || pcm48kMono == null || pcm48kMono.Length == 0)
				return;

			var bytes = new byte[pcm48kMono.Length * 2];
			Buffer.BlockCopy(pcm48kMono, 0, bytes, 0, bytes.Length);
			_txBuffer.AddSamples(bytes, 0, bytes.Length);
		}

		public void StopTransmit()
		{
			if (_waveOut == null)
				return;
			try { _waveOut.Stop(); } catch { /* ignore */ }
			_waveOut.Dispose();
			_waveOut = null;
			_txBuffer = null;
		}

		public void Dispose()
		{
			StopReceive();
			StopTransmit();
		}
	}
}
