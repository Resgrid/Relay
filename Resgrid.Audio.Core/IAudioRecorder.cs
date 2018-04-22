using System;
using NAudio.Wave;

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
	}
}