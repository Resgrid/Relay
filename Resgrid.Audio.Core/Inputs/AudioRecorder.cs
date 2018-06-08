// https://github.com/markheath/voicerecorder

using NAudio.Mixer;
using NAudio.Wave;
using System;
using System.Linq;

namespace Resgrid.Audio.Core
{
	public class AudioRecorder : IAudioRecorder
	{
		WaveInEvent waveIn;
		private readonly SampleAggregator sampleAggregator;
		double desiredVolume = 100;
		RecordingState recordingState;
		WaveFileWriter writer;
		WaveFormat recordingFormat;
		BufferedWaveProvider bwp;

		private AudioEvaluator audioEvaluator;

		public event EventHandler Stopped = delegate { };

		private int RATE = 44100; // sample rate of the sound card
		private int BUFFERSIZE = (int)Math.Pow(2, 11); // must be a multiple of 2

		public AudioRecorder()
		{
			audioEvaluator = new AudioEvaluator();
			sampleAggregator = new SampleAggregator();
			RecordingFormat = new WaveFormat(RATE, 1);
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
				sampleAggregator.NotificationCount = value.SampleRate / 10;
			}
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
			//waveIn.BufferMilliseconds = (int)((double)BUFFERSIZE / (double)RATE * 1000.0);
			waveIn.BufferMilliseconds = 5120;

			bwp = new BufferedWaveProvider(waveIn.WaveFormat);
			bwp.BufferLength = BUFFERSIZE * 2;
			bwp.DiscardOnBufferOverflow = true;

			audioEvaluator.Start(waveIn);
			waveIn.StartRecording();
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

		public SampleAggregator SampleAggregator => sampleAggregator;

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

			sampleAggregator.OnDataAvailable(buffer, bytesRecorded);

			for (int index = 0; index < e.BytesRecorded; index += 2)
			{
				short sample = (short)((buffer[index + 1] << 8) | buffer[index + 0]);
				float sample32 = sample / 32768f;
				sampleAggregator.Add(sample32);

			}

			int frameSize = BUFFERSIZE;
			byte[] frames = new byte[frameSize];

			bwp.Read(frames, 0, frameSize);
			sampleAggregator.Calculate(frames, frameSize);


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
	}
}
