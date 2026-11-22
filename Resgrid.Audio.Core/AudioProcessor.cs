using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Resgrid.Audio.Core.Events;
using Serilog;
using Timer = System.Timers.Timer;
using ElapsedEventArgs = System.Timers.ElapsedEventArgs;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public interface IAudioProcessor
	{
		void Init(Config config);
		void Start();
	}

	public class AudioProcessor : IAudioProcessor
	{
		// Per instance: the desktop app stops and starts audio mode in one process, and a stopped pipeline must not share its
		// lock or "timer busy" flag with the new one.
		private readonly object _lock = new object();
		private int _timerProcessing;
		private volatile bool _stopped;

		private bool _initialized = false;
		private Config _config;
		private readonly Dictionary<Guid, Watcher> _startedWatchers;

		private readonly IAudioRecorder _audioRecorder;
		private readonly IAudioEvaluator _audioEvaluator;
		private readonly IWatcherAudioStorage _watcherAudioStorage;
		private readonly SampleAggregator _sampleAggregator;
		private readonly ILogger _logger;
		private readonly Timer _timer;

		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingStarted;
		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingFinished;

		private int _checkCount = 0;

		public AudioProcessor(IAudioRecorder audioRecorder, IAudioEvaluator audioEvaluator, IWatcherAudioStorage watcherAudioStorage, ILogger logger = null)
		{
			_audioRecorder = audioRecorder;
			_audioEvaluator = audioEvaluator;
			_watcherAudioStorage = watcherAudioStorage;
			_logger = logger;

			_sampleAggregator = new SampleAggregator();
			_startedWatchers = new Dictionary<Guid, Watcher>();

			_audioRecorder.SetSampleAggregator(_sampleAggregator);

			_timer = new Timer(1000);
			_timer.Elapsed += _timer_Elapsed;
		}

		/// <summary>
		/// Finishes every watcher whose recording window has passed: encodes its audio and raises
		/// <see cref="TriggerProcessingFinished"/> (which creates the Resgrid call). The finished watchers are taken out under the
		/// lock, but the slow work (MP3 encoding, the Resgrid HTTP calls in the handler) runs outside it, so capture and tone
		/// detection keep running and a page that follows immediately is not missed. A failure is logged and never leaves the
		/// pipeline wedged: System.Timers.Timer swallows exceptions, so before this a single throw left the busy flag set and the
		/// watcher active forever, and no further calls were ever created.
		/// </summary>
		private void _timer_Elapsed(object sender, ElapsedEventArgs e)
		{
			if (_stopped || Interlocked.Exchange(ref _timerProcessing, 1) == 1)
				return;

			try
			{
				List<Watcher> finished = null;
				lock (_lock)
				{
					if (_startedWatchers.Count > 0)
					{
						var now = DateTime.UtcNow;
						finished = new List<Watcher>();
						foreach (var startedWatcher in _startedWatchers.Values)
						{
							startedWatcher.LastCheckedTimestamp = now;
							if ((now - startedWatcher.TriggerFiredTimestamp).TotalSeconds >= _config.AudioLength + 5)
								finished.Add(startedWatcher);
						}

						foreach (var watcher in finished)
							_startedWatchers.Remove(watcher.Id);
					}
				}

				if (finished == null)
				{
					CleanUpCheck();
					return;
				}

				foreach (var watcher in finished)
					FinishWatcher(watcher);
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Audio watcher processing failed");
			}
			finally
			{
				Interlocked.Exchange(ref _timerProcessing, 0);
			}
		}

		private void FinishWatcher(Watcher watcher)
		{
			try
			{
				var mp3Audio = _audioRecorder.SaveWatcherAudio(watcher);
				_watcherAudioStorage.FinishWatcher(watcher.Id);
				TriggerProcessingFinished?.Invoke(this, new TriggerProcessedEventArgs(watcher, watcher.GetTrigger(), DateTime.UtcNow, mp3Audio));
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to finish the dispatch for watcher {Watcher}", watcher.Name);
			}
			finally
			{
				// Release the watchers that rode along on this recording too (Multiple=false): they were marked active when they
				// triggered, and left that way they were skipped by the evaluator forever and paged again with every later call.
				_audioEvaluator.RemoveActiveWatcher(watcher.Id);
				foreach (var additional in watcher.GetAdditionalWatchers() ?? new List<Watcher>())
					_audioEvaluator.RemoveActiveWatcher(additional.Id);

				watcher.Clear();
			}
		}

		/// <summary>
		/// Stops the processing timer (the recorder is stopped separately). Waits, bounded, for an in-flight tick so its
		/// Resgrid requests finish before the caller disposes the API client, then dispatches any watcher still inside its
		/// recording window with the audio captured so far: a page that arrived just before a stop was otherwise dropped
		/// without a trace.
		/// </summary>
		public void Stop()
		{
			if (_stopped)
				return;
			_stopped = true;
			_timer.Enabled = false;

			var deadline = DateTime.UtcNow.AddSeconds(30);
			while (Volatile.Read(ref _timerProcessing) == 1 && DateTime.UtcNow < deadline)
				Thread.Sleep(50);

			List<Watcher> pending;
			lock (_lock)
			{
				pending = _startedWatchers.Values.ToList();
				_startedWatchers.Clear();
			}

			foreach (var watcher in pending)
			{
				_logger?.Warning("Stopping: dispatching watcher {Watcher} now with the audio recorded so far", watcher.Name);
				FinishWatcher(watcher);
			}

			_timer.Dispose();
		}

		public void Init(Config config)
		{
			_config = config;

			if (_sampleAggregator != null && !_initialized)
			{
				_sampleAggregator.DataAvailable += _sampleAggregator_DataAvailable;
				_audioEvaluator.WatcherTriggered += _audioEvaluator_WatcherTriggered;

				_audioEvaluator.Init(_config);

				_timer.Interval = 1000;
				_timer.Enabled = true;

				_initialized = true;
			}
		}

		private void _audioEvaluator_WatcherTriggered(object sender, WatcherEventArgs e)
		{
			lock (_lock)
			{
				AddTriggeredWatcher(e.Watcher, e.Triggers.FirstOrDefault(), e.Timestamp);
			}
		}

		public void Start()
		{
			Init(_config);
			_audioRecorder.BeginMonitoring(_config.InputDevice);
		}

		private void _sampleAggregator_DataAvailable(object sender, DataAvailableArgs e)
		{
			if (e?.Buffer == null)
				return;

			// Snapshot under the lock: a trigger on the detector thread adds to the dictionary while this runs on the capture
			// thread, and enumerating it live threw "Collection was modified".
			Guid[] recordingWatchers;
			lock (_lock)
			{
				if (_startedWatchers.Count == 0)
					return;

				recordingWatchers = _startedWatchers.Keys.ToArray();
			}

			for (int index = 0; index + 1 < e.BytesRecorded; index += 2)
			{
				short sample = (short)((e.Buffer[index + 1] << 8) | e.Buffer[index + 0]);
				float sample32 = sample / 32768f;

				if (!IsSilence(sample32, _config.Threshold))
				{
					var data = new byte[] { e.Buffer[index + 0], e.Buffer[index + 1] };
					foreach (var watcherId in recordingWatchers)
						_watcherAudioStorage.AddAudio(watcherId, data);
				}
			}
		}

		private void AddTriggeredWatcher(Watcher watcher, Trigger trigger, DateTime timeStamp)
		{
			if (watcher != null)
			{
				if (_startedWatchers.Count > 0 && !_config.Multiple)
				{
					_startedWatchers.First().Value.AddAdditionalWatcher(watcher);
					_audioEvaluator.AddActiveWatcher(watcher.Id);
				}
				else if (!_startedWatchers.ContainsKey(watcher.Id))
				{
					watcher.TriggerFiredTimestamp = timeStamp;
					watcher.LastCheckedTimestamp = timeStamp;
					watcher.SetFiredTrigger(trigger);

					_watcherAudioStorage.StartNewAudio(watcher.Id, null);
					_startedWatchers.Add(watcher.Id, watcher);
					_audioEvaluator.AddActiveWatcher(watcher.Id);
				}
			}
		}

		private void CleanUpCheck()
		{
			// Time1 gating this so were not constantly hitting the triggers
			if (_checkCount >= 60)
			{
				if (_startedWatchers == null || _startedWatchers.Count <= 0)
				{
					_audioEvaluator.CleanUpTones();
					_watcherAudioStorage.Clear();
				}

				_checkCount = 0;
			}
			else
			{
				_checkCount++;
			}
		}

		private bool IsSilence(float amplitude, sbyte threshold)
		{
			if (!_config.EnableSilenceDetection)
				return false;

			double dB = 20 * Math.Log10(Math.Abs(amplitude));
			var isSilence = dB < threshold;

			return isSilence;
		}
	}
}
