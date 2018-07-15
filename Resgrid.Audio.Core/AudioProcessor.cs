using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Accord.Math.Optimization;
using Resgrid.Audio.Core.Events;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public class AudioProcessor: IAudioProcessor
	{
		private const int BUFFER_SIZE = 26400000; // 5 Minute Buffer, 1 second = 88,000 array elements
		private const int ONE_SEC = 88000;

		private bool _initialized = false;
		private Queue<byte> _buffer;
		private Settings _settings;

		private Config _config;
		private Dictionary<Guid, Watcher> _startedWatchers;

		private readonly IAudioRecorder _audioRecorder;
		private readonly IAudioEvaluator _audioEvaluator;
		private readonly SampleAggregator _sampleAggregator;
		private Timer _timer;

		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingStarted;
		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingFinished;

		public AudioProcessor(IAudioRecorder audioRecorder, IAudioEvaluator audioEvaluator)
		{
			_audioRecorder = audioRecorder;
			_audioEvaluator = audioEvaluator;

			_buffer = new Queue<byte>(BUFFER_SIZE);
			_sampleAggregator = new SampleAggregator();
			_startedWatchers = new Dictionary<Guid, Watcher>();

			_timer = new Timer(1000);
			_timer.Elapsed += _timer_Elapsed;
		}

		private void _timer_Elapsed(object sender, ElapsedEventArgs e)
		{
			// This timer is the first pass, _sampleAggregator_DataAvailable looking for silence is a better long term pattern.
			if (_startedWatchers != null && _startedWatchers.Any())
			{
				List<Guid> watchersToRemove = new List<Guid>();

				foreach (var startedWatcher in _startedWatchers)
				{
					var diffInSeconds = (DateTime.UtcNow - startedWatcher.Value.LastCheckedTimestamp).TotalSeconds;
					startedWatcher.Value.AddAudio(_buffer.Take((int)diffInSeconds * ONE_SEC).ToArray());

					startedWatcher.Value.LastCheckedTimestamp = DateTime.UtcNow;

					if ((DateTime.UtcNow - startedWatcher.Value.TriggerFiredTimestamp).TotalSeconds >= _config.AudioLength)
					{
						watchersToRemove.Add(startedWatcher.Value.Id);
					}
				}

				foreach (var id in watchersToRemove)
				{
					var watcher = _startedWatchers[id];
					TriggerProcessingFinished?.Invoke(this, new TriggerProcessedEventArgs(watcher, watcher.GetTrigger(), DateTime.UtcNow));

					_startedWatchers.Remove(id);
				}
			}
		}

		public void Init(Config config)
		{
			_config = config;

			if (_sampleAggregator != null && !_initialized)
			{
				_sampleAggregator.WaveformCalculated += _sampleAggregator_WaveformCalculated;
				_sampleAggregator.DataAvailable += _sampleAggregator_DataAvailable;
				_audioEvaluator.WatcherTriggered += _audioEvaluator_WatcherTriggered;
				
				_audioEvaluator.Init(_config.Watchers);

				_timer.Interval = config.AudioLength;
				_timer.Enabled = true;

				_initialized = true;
			}
		}

		private void _audioEvaluator_WatcherTriggered(object sender, WatcherEventArgs e)
		{
			AddTriggeredWatcher(e.Watcher, e.Triggers.FirstOrDefault(), _buffer.Take(1320000).ToArray(), e.Timestamp);
		}

		public void Start()
		{
			Init(_config);
			_audioRecorder.BeginMonitoring(_config.InputDevice);
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

				/*
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
				*/
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

		private void AddTriggeredWatcher(Watcher watcher, Trigger trigger, byte[] audio, DateTime timeStamp)
		{
			if (watcher != null && !_startedWatchers.ContainsKey(watcher.Id))
			{
				watcher.TriggerFiredTimestamp = timeStamp;
				watcher.LastCheckedTimestamp = timeStamp;
				watcher.SetFiredTrigger(trigger);
				watcher.AddAudio(audio);
				_startedWatchers.Add(watcher.Id, watcher);
			}
		}
	}
}
