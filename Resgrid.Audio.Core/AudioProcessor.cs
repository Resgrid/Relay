using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
		// Both guarded by _lock. The tick claims _tickInFlight in the same lock Stop sets _stopped in, so a tick is either
		// admitted before the stop (and Stop waits for it) or sees the stop and returns.
		private TaskCompletionSource<bool> _tickInFlight;
		private bool _stopped;

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
			// One tick at a time (System.Timers.Timer overlaps a slow handler), claimed in the lock Stop sets _stopped in.
			TaskCompletionSource<bool> tick;
			lock (_lock)
			{
				if (_stopped || _tickInFlight != null)
					return;

				tick = _tickInFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			}

			try
			{
				var finished = new List<Watcher>();
				lock (_lock)
				{
					var now = DateTime.UtcNow;
					foreach (var startedWatcher in _startedWatchers.Values)
					{
						startedWatcher.LastCheckedTimestamp = now;
						if ((now - startedWatcher.TriggerFiredTimestamp).TotalSeconds >= _config.AudioLength + 5)
							finished.Add(startedWatcher);
					}

					foreach (var watcher in finished)
						_startedWatchers.Remove(watcher.Id);
				}

				foreach (var watcher in finished)
					FinishWatcher(watcher);

				// Every tick counts toward the cleanup, busy or idle: counting only idle ticks meant a pipeline that was paged
				// back to back never swept its stale tones.
				CleanUpCheck();
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Audio watcher processing failed");
			}
			finally
			{
				lock (_lock)
					_tickInFlight = null;
				tick.TrySetResult(true);
			}
		}

		private void FinishWatcher(Watcher watcher)
		{
			try
			{
				byte[] mp3Audio;
				try
				{
					mp3Audio = _audioRecorder.SaveWatcherAudio(watcher);
				}
				finally
				{
					// Drop the buffer whether or not the encoding worked, before the watcher is released below: StartNewAudio keeps
					// a buffer it finds, so one left behind by a failed encode was sent again with the watcher's next dispatch.
					_watcherAudioStorage.FinishWatcher(watcher.Id);
				}

				TriggerProcessingFinished?.Invoke(this, new TriggerProcessedEventArgs(watcher, watcher.GetTrigger(), DateTime.UtcNow, mp3Audio));
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to finish the dispatch for watcher {Watcher}", watcher.Name);
			}
			finally
			{
				// Snapshot the riders and clear this dispatch BEFORE releasing any id. A released watcher can page again at once
				// and start a new recording on this same Watcher object (and another watcher can ride along on it); clearing
				// after the release erased that new dispatch's trigger and riders, and a rider erased that way was never
				// released, so the evaluator skipped its later pages.
				var riders = watcher.GetAdditionalWatchers()?.ToList() ?? new List<Watcher>();
				watcher.Clear();

				// Release the watchers that rode along on this recording too (Multiple=false): they were marked active when they
				// triggered, and left that way they were skipped by the evaluator forever and paged again with every later call.
				_audioEvaluator.RemoveActiveWatcher(watcher.Id);
				foreach (var rider in riders)
					_audioEvaluator.RemoveActiveWatcher(rider.Id);
			}
		}

		/// <summary>
		/// Stops the processing timer (the recorder is stopped separately). Waits, bounded, for an in-flight tick, then
		/// dispatches any watcher still inside its recording window with the audio captured so far: a page that arrived just
		/// before a stop was otherwise dropped without a trace. A tick still sending its dispatch after the wait carries on in
		/// the background; await <see cref="WaitForInFlightDispatchAsync"/> before disposing the API client it uses.
		/// </summary>
		public void Stop()
		{
			Task inFlight;
			lock (_lock)
			{
				if (_stopped)
					return;

				_stopped = true;
				inFlight = _tickInFlight?.Task;
			}
			_timer.Enabled = false;

			if (inFlight != null && !inFlight.Wait(TimeSpan.FromSeconds(30)))
				_logger?.Warning("Stopping: a dispatch is still being sent to Resgrid after 30s; it is left to finish");

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

		/// <summary>
		/// Completes when the timer tick in flight (if any) has finished sending its dispatch. After <see cref="Stop"/> no new
		/// tick starts, so this is the one Stop may have stopped waiting for: the caller awaits it before disposing the API
		/// client, which otherwise failed that dispatch midway (a call saved without its audio).
		/// </summary>
		public Task WaitForInFlightDispatchAsync()
		{
			lock (_lock)
				return _tickInFlight?.Task ?? Task.CompletedTask;
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
				// Stop has already dispatched what was recording and nothing would ever finish this one (it would also stay
				// marked active in the evaluator).
				if (_stopped)
				{
					_logger?.Warning("Ignoring watcher {Watcher} triggered while the audio pipeline is stopping", e.Watcher?.Name);
					return;
				}

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

			// Appended in the same lock that starts and finishes recordings, so a block goes only to the recordings live at that
			// moment. A snapshot of watcher ids taken here and appended after the lock was released could land in a NEW
			// recording opened under the same id once the old one had finished. (Enumerating the dictionary outside the lock
			// also threw "Collection was modified" when the detector thread added a watcher.)
			lock (_lock)
			{
				if (_startedWatchers.Count == 0)
					return;

				var audio = GetRecordableAudio(e.Buffer, e.BytesRecorded);
				if (audio.Length == 0)
					return;

				foreach (var watcherId in _startedWatchers.Keys)
					_watcherAudioStorage.AddAudio(watcherId, audio);
			}
		}

		/// <summary>The block's 16-bit samples, less the silent ones when silence detection is on.</summary>
		private byte[] GetRecordableAudio(byte[] buffer, int bytesRecorded)
		{
			var length = Math.Min(bytesRecorded, buffer.Length);
			var audio = new List<byte>(length);
			for (int index = 0; index + 1 < length; index += 2)
			{
				short sample = (short)((buffer[index + 1] << 8) | buffer[index + 0]);
				float sample32 = sample / 32768f;

				if (!IsSilence(sample32, _config.Threshold))
				{
					audio.Add(buffer[index + 0]);
					audio.Add(buffer[index + 1]);
				}
			}

			return audio.ToArray();
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

		/// <summary>
		/// About once a minute, drops tones the evaluator has held for over five minutes. Runs on the timer thread with no
		/// processor lock held (the trigger callback takes the evaluator's lock and then this one). The tone sweep is by age
		/// only, so it is safe while a watcher is recording. The audio buffers are no longer wiped here: each recording's
		/// buffer is removed when it finishes, and the blanket clear raced a page that started after the idle check, removing
		/// its new buffer so the call was skipped as empty.
		/// </summary>
		private void CleanUpCheck()
		{
			// Gated so we are not constantly sweeping the tone bank.
			if (_checkCount >= 60)
			{
				_audioEvaluator.CleanUpTones();
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
