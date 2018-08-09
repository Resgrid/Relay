// https://github.com/markheath/voicerecorder

using NAudio.Mixer;
using NAudio.Wave;
using System;
using System.IO;
using System.Linq;
using NAudio.Lame;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public interface IAudioRecorder
	{
		void BeginMonitoring(int recordingDevice);
		void BeginRecording(string path);
		void Stop();
		RecordingState RecordingState { get; }
		SampleAggregator SampleAggregator { get; }
		event EventHandler Stopped;
		WaveFormat RecordingFormat { get; set; }
		TimeSpan RecordedTime { get; }
		void SetSampleAggregator(SampleAggregator sampleAggregator);
		byte[] SaveWatcherAudio(Watcher watcher);
	}

	public class AudioRecorder : IAudioRecorder
	{
		WaveInEvent waveIn;
		private SampleAggregator _sampleAggregator;
		double desiredVolume = 100;
		RecordingState recordingState;
		WaveFileWriter writer;
		WaveFormat recordingFormat;
		BufferedWaveProvider bwp;

		private AudioEvaluator _audioEvaluator;

		public event EventHandler Stopped = delegate { };

		private int RATE = 44100; // 44100 is a pretty standard rate, but for Speech-to-Text they almost always want 16000
		private int BUFFERSIZE = (int)Math.Pow(2, 11); // must be a multiple of 2

		public AudioRecorder(AudioEvaluator audioEvaluator)
		{
			_audioEvaluator = audioEvaluator;
		}

		public WaveFormat RecordingFormat
		{
			get
			{
				return recordingFormat;
			}
			set
			{
				recordingFormat = value;
				_sampleAggregator.NotificationCount = value.SampleRate / 10;
			}
		}

		public void SetSampleAggregator(SampleAggregator sampleAggregator)
		{
			_sampleAggregator = sampleAggregator;
			RecordingFormat = new WaveFormat(RATE, 1);
		}

		public void BeginMonitoring(int recordingDevice)
		{
			if (recordingState != RecordingState.Stopped)
			{
				throw new InvalidOperationException("Can't begin monitoring while we are in this state: " + recordingState.ToString());
			}

			waveIn = new WaveInEvent();
			waveIn.DeviceNumber = recordingDevice;
			waveIn.DataAvailable += OnDataAvailable;
			waveIn.RecordingStopped += OnRecordingStopped;
			waveIn.WaveFormat = recordingFormat;
			//wi.WaveFormat = new NAudio.Wave.WaveFormat(RATE, 1);
			//waveIn.BufferMilliseconds = BUFFERSIZE; //(int)((double)BUFFERSIZE / (double)RATE * 1000.0);
			waveIn.BufferMilliseconds = (int)((double)BUFFERSIZE / (double)RATE * 1000.0);
			//waveIn.BufferMilliseconds = 5120;

			bwp = new BufferedWaveProvider(waveIn.WaveFormat);
			bwp.BufferLength = BUFFERSIZE * 2;
			bwp.DiscardOnBufferOverflow = true;

			waveIn.StartRecording();
			_audioEvaluator.Start(new WaveInEvent() {DeviceNumber = recordingDevice});
			//_audioEvaluator.Start(waveIn);

			recordingState = RecordingState.Monitoring;
		}

		void OnRecordingStopped(object sender, StoppedEventArgs e)
		{
			recordingState = RecordingState.Stopped;
			writer.Dispose();
			Stopped(this, EventArgs.Empty);
		}

		public void BeginRecording(string waveFileName)
		{
			//if (recordingState != RecordingState.Monitoring)
			//{
			//	throw new InvalidOperationException("Can't begin recording while we are in this state: " + recordingState.ToString());
			//}
			writer = new WaveFileWriter(waveFileName, recordingFormat);
			recordingState = RecordingState.Recording;
		}

		public void Stop()
		{
			if (recordingState == RecordingState.Recording)
			{
				recordingState = RecordingState.RequestedStop;
				waveIn.StopRecording();
			}
		}

		public SampleAggregator SampleAggregator => _sampleAggregator;

		public RecordingState RecordingState => recordingState;

		public TimeSpan RecordedTime
		{
			get
			{
				if (writer == null)
				{
					return TimeSpan.Zero;
				}
				return TimeSpan.FromSeconds((double)writer.Length / writer.WaveFormat.AverageBytesPerSecond);
			}
		}

		void OnDataAvailable(object sender, WaveInEventArgs e)
		{
			bwp.AddSamples(e.Buffer, 0, e.BytesRecorded);

			byte[] buffer = e.Buffer;
			int bytesRecorded = e.BytesRecorded;
			//WriteToFile(buffer, bytesRecorded);

			_sampleAggregator.OnDataAvailable(buffer, bytesRecorded);

			for (int index = 0; index < e.BytesRecorded; index += 2)
			{
				short sample = (short)((buffer[index + 1] << 8) | buffer[index + 0]);
				float sample32 = sample / 32768f;
				_sampleAggregator.Add(sample32);
			}

			int frameSize = BUFFERSIZE;
			byte[] frames = new byte[frameSize];

			bwp.Read(frames, 0, frameSize);
			_sampleAggregator.Calculate(frames, frameSize);
		}

		private void WriteToFile(byte[] buffer, int bytesRecorded)
		{
			long maxFileLength = this.recordingFormat.AverageBytesPerSecond * 60;

			if (recordingState == RecordingState.Recording
					|| recordingState == RecordingState.RequestedStop)
			{
				var toWrite = (int)Math.Min(maxFileLength - writer.Length, bytesRecorded);
				if (toWrite > 0)
				{
					writer.Write(buffer, 0, bytesRecorded);
				}
				else
				{
					Stop();
				}
			}
		}

		public byte[] SaveWatcherAudio(Watcher watcher)
		{
			var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			var fileName = $"{path}\\DispatchAudio\\RelayAudio_{DateTime.Now.ToString("s").Replace(":", "_")}.wav";

			var waveWriter = new WaveFileWriter(fileName, recordingFormat);

			var buffer = watcher.GetBuffer();
			waveWriter.Write(buffer, 0, buffer.Length);
			waveWriter.Dispose();

			using (var retMs = new MemoryStream())
			using (var ms = new MemoryStream())
			using (var rdr = new WaveFileReader(fileName))
			using (var wtr = new LameMP3FileWriter(retMs, rdr.WaveFormat, RATE / 10))
			{
				rdr.CopyTo(wtr);
				return retMs.ToArray();
			}
		}
	}
}
