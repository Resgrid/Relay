using System;
using System.Collections.Generic;
using System.Linq;
using Resgrid.Audio.Core.Events;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public class AudioProcessor: IAudioProcessor
	{
		private bool _initialized = false;
		private const int BUFFER_SIZE = 26400000; // 5 Minute Buffer, 1 second = 88,000 array elements
		private Queue<byte> _buffer;
		private Settings _settings;

		private List<Watcher> _watchers;
		private Dictionary<Guid, Watcher> _startedWatchers;

		private readonly IAudioRecorder _audioRecorder;
		private readonly IAudioEvaluator _audioEvaluator;
		private readonly SampleAggregator _sampleAggregator;

		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingStarted;
		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingFinished;

		public AudioProcessor(IAudioRecorder audioRecorder, IAudioEvaluator audioEvaluator)
		{
			_audioRecorder = audioRecorder;
			_audioEvaluator = audioEvaluator;

			_buffer = new Queue<byte>(BUFFER_SIZE);
			_sampleAggregator = new SampleAggregator();
			_startedWatchers = new Dictionary<Guid, Watcher>();
			_watchers = new List<Watcher>();
		}

		public void Init(List<Watcher> watchers)
		{
			if (_sampleAggregator != null && !_initialized)
			{
				_sampleAggregator.WaveformCalculated += _sampleAggregator_WaveformCalculated;
				_sampleAggregator.DataAvailable += _sampleAggregator_DataAvailable;
				_audioEvaluator.WatcherTriggered += _audioEvaluator_WatcherTriggered;

				_audioEvaluator.Init(watchers);

				_initialized = true;
			}
		}

		private void _audioEvaluator_WatcherTriggered(object sender, WatcherEventArgs e)
		{
			AddTriggeredWatcher(e.Watcher, e.Triggers.FirstOrDefault(), _buffer.Take(1320000).ToArray());
		}

		public void Start()
		{
			Init(_watchers);
			_audioRecorder.BeginMonitoring(0);
		}

		private void _sampleAggregator_DataAvailable(object sender, DataAvailableArgs e)
		{
			List<Guid> watchersToRemove = new List<Guid>();

			if (e?.Buffer != null)
			{
				foreach (var buffer in e.Buffer)
				{
					_buffer.Enqueue(buffer);
				}

				foreach (var watcher in _startedWatchers)
				{
					if (!watcher.Value.AddAudio(e.Buffer))
					{
						if (TriggerProcessingFinished != null)
						{
							watchersToRemove.Add(watcher.Value.Id);
							TriggerProcessingFinished(this, new TriggerProcessedEventArgs(watcher.Value, watcher.Value.GetTrigger(), DateTime.UtcNow));
						}
					}
				}

				foreach (var id in watchersToRemove)
				{
					_startedWatchers.Remove(id);
				}
			}
		}

		private void _sampleAggregator_WaveformCalculated(object sender, WaveformEventArgs e)
		{
			if (_settings != null && _settings.Watchers != null)
			{
				foreach (var watcher in _settings.Watchers)
				{
					if (watcher.Triggers != null)
					{
						foreach (var trigger in watcher.Triggers)
						{
							//if (_audioEvaluator.EvaluateAudioTrigger(trigger, e.FastFourierTransform))
							//{
							//	if (TriggerProcessingStarted != null)
							//	{
							//		AddTriggeredWatcher(watcher, trigger, _buffer.Take(1320000).ToArray());
							//		TriggerProcessingStarted(this, new TriggerProcessedEventArgs(watcher, trigger, DateTime.UtcNow));
							//	}
							//}
						}
					}
				}
			}
		}

		private void AddTriggeredWatcher(Watcher watcher, Trigger trigger, byte[] audio)
		{
			if (watcher != null && !_startedWatchers.ContainsKey(watcher.Id))
			{
				watcher.SetFiredTrigger(trigger);
				watcher.AddAudio(audio);
				_startedWatchers.Add(watcher.Id, watcher);
			}
		}
	}
}
