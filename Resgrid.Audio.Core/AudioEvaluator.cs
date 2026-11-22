using Resgrid.Audio.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Accord.Statistics.Models;
using DtmfDetection;
using DtmfDetection.NAudio;
using NAudio.Wave;
using Resgrid.Audio.Core.Events;
using Serilog;

namespace Resgrid.Audio.Core
{
	public interface IAudioEvaluator
	{
		event EventHandler<EvaluatorEventArgs> EvaluatorStarted;
		event EventHandler<EvaluatorEventArgs> EvaluatorFinished;
		event EventHandler<WatcherEventArgs> WatcherTriggered;

		void Init(Config config);
		void Start(IWaveIn waveIn);
		void ClearTones();
		int CleanUpTones();
		void AddActiveWatcher(Guid watcherId);
		void RemoveActiveWatcher(Guid watcherId);
		void Stop();
	}

	public class AudioEvaluator : IAudioEvaluator
	{
		// Per instance (they were static): after a stop/start in the same process the old pipeline's detector shared the new
		// one's tone bank and active-watcher list, consuming its tones and blocking its watchers.
		private readonly object _lock = new object();
		private readonly ILogger _logger;

		private Config _config;
		private List<DtmfTone> _dtmfTone;
		private readonly List<DtmfToneEnd> _finishedTones;
		private readonly List<Guid> _startedWatchers;
		private LiveAudioDtmfAnalyzer _analyzer;

		public event EventHandler<EvaluatorEventArgs> EvaluatorStarted;
		public event EventHandler<EvaluatorEventArgs> EvaluatorFinished;
		public event EventHandler<WatcherEventArgs> WatcherTriggered;

		public AudioEvaluator(ILogger logger)
		{
			_logger = logger;

			_dtmfTone = new List<DtmfTone>();
			_finishedTones = new List<DtmfToneEnd>();
			_startedWatchers = new List<Guid>();
		}

		public void Init(Config config)
		{
			_config = config;

			if (_config.Watchers != null && _config.Watchers.Any())
			{
				PureTones.ClearTones();
				DtmfClassification.ClearAllTones();

				var activeWatchers = config.Watchers.Where(x => x.Active).ToList();

				foreach (var watcher in activeWatchers)
				{
					if (watcher.Triggers != null && watcher.Triggers.Any())
					{
						foreach (var trigger in watcher.Triggers)
						{
							if (trigger != null)
							{
								if (trigger.Count >= 1)
								{
									var addedTone = AddTone(Trigger.ToneKey(trigger.Frequency1));
									trigger.Tones.Add(addedTone);
								}

								if (trigger.Count >= 2)
								{
									var addedTone = AddTone(Trigger.ToneKey(trigger.Frequency2));
									trigger.Tones.Add(addedTone);
								}
							}
						}
					}
				}
			}
		}

		//public void Start(BufferedWaveProvider provider)
		//{
		//	var config = new DetectorConfig();
		//	var dtmfAudio = DtmfAudio.CreateFrom(new StreamingSampleSource(config, provider, false), config);
		//	var detectedTones = new Queue<DtmfOccurence>();

		//	LiveAudioDtmfAnalyzer();

		//	dtmfAudio.Forward(
		//		(channel, tone) => waveFile.CurrentTime,
		//		(channel, start, tone) => detectedTones.Enqueue(new DtmfOccurence(tone, channel, start, waveFile.CurrentTime - start)));
		//}

		public void ClearTones()
		{
			_logger.Debug("AudioEvaluator->Clearing Tones");

			lock (_lock)
			{
				_finishedTones.Clear();
			}
		}

		public void AddActiveWatcher(Guid watcherId)
		{
			lock (_lock)
			{
				if (!_startedWatchers.Contains(watcherId))
					_startedWatchers.Add(watcherId);
			}
		}

		public void RemoveActiveWatcher(Guid watcherId)
		{
			lock (_lock)
			{
				_startedWatchers.Remove(watcherId);
			}
		}

		/// <summary>Stops the tone detector's own capture device; without this it kept listening after the mode was stopped.</summary>
		public void Stop()
		{
			var analyzer = _analyzer;
			_analyzer = null;
			try
			{
				analyzer?.StopCapturing();
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Stopping the tone detector failed");
			}
		}

		public void Start(IWaveIn waveIn)
		{
			var config = new DetectorConfig();
			config.PowerThreshold = _config.Tolerance;

			var analyzer = new LiveAudioDtmfAnalyzer(config, waveIn);
			_analyzer = analyzer;

			//analyzer.DtmfToneStarted += start => _log.Add($"{start.DtmfTone.Key} key started on {start.Position.TimeOfDay} (channel {start.Channel})");
			//analyzer.DtmfToneStopped += end => _log.Add($"{end.DtmfTone.Key} key stopped after {end.Duration.TotalSeconds}s (channel {end.Channel})");

			analyzer.DtmfToneStarted += start =>
			{
				EvaluatorStarted?.Invoke(this, new EvaluatorEventArgs(start, null, DateTime.UtcNow));
			};

			analyzer.DtmfToneStopped += end =>
			{
				_logger.Debug($"AudioEvaluator->DtmfToneStopped {end.TimeStamp.ToString("O")}");

				EvaluatorFinished?.Invoke(this, new EvaluatorEventArgs(null, end, DateTime.UtcNow));

				try
				{
					if (end.Duration > new TimeSpan(0, 0, 0, 0, 0))
					{
						// Every _finishedTones access is under the lock: the once-a-minute CleanUpTones on the timer thread removes
						// from the same list, and a tone ending during that sweep threw "Collection was modified" into the analyzer.
						lock (_lock)
						{
							_logger.Debug($"AudioEvaluator->Finished Tones: {_finishedTones.Count}");
							foreach (var tone in _finishedTones)
							{
								_logger.Debug(
									$"AudioEvaluator->Finished Tone: {tone.DtmfTone.HighTone} finished in {tone.Duration.TotalMilliseconds}ms");
							}

							var existingTone = _finishedTones.FirstOrDefault(x => x.DtmfTone.HighTone == end.DtmfTone.HighTone &&
																			 (end.TimeStamp.Subtract(x.TimeStamp).TotalMilliseconds <= 1000 &&
																			  end.TimeStamp.Subtract(x.TimeStamp).TotalMilliseconds >= -1000));

							if (existingTone != null && existingTone.Duration < new TimeSpan(0, 0, 0, 0, 1500))
							{
								_logger.Debug($"AudioEvaluator->DtmfToneStopped: Existing tone for {existingTone.DtmfTone.HighTone} adding {end.Duration.TotalMilliseconds}ms");
								existingTone.Duration += end.Duration;
							}
							else
							{
								_logger.Debug($"AudioEvaluator->DtmfToneStopped: No Existing tone for {end.DtmfTone.HighTone} for duration {end.Duration}");
								_finishedTones.Add(end);
							}
						}
					}

					CheckFinishedTonesForTriggers();
				}
				catch (Exception ex)
				{
					// Never let a failure escape into the analyzer's capture callback: that could stop tone detection while the
					// mode still reports Running.
					_logger?.Error(ex, "Tone evaluation failed");
				}
			};

			analyzer.StartCapturing();
		}

		private DtmfTone AddTone(int frequency)
		{
			PureTones.TryAddHighTone(frequency);

			var tone = _dtmfTone.FirstOrDefault(x => x.HighTone == frequency);

			if (tone == DtmfTone.None)
			{
				tone = new DtmfTone(frequency, 0, (PhoneKey)(_dtmfTone.Count() + (int)PhoneKey.Custom1));
				DtmfClassification.AddCustomTone(tone);
				_dtmfTone.Add(tone);
			}

			return tone;
		}

		public int CleanUpTones()
		{
			List<DtmfToneEnd> tonesToCleanUp = new List<DtmfToneEnd>();

			lock (_lock)
			{
				foreach (var tone in _finishedTones)
				{
					if (DateTime.Now.Subtract(tone.TimeStamp).TotalMinutes > 5)
						tonesToCleanUp.Add(tone);
				}

				foreach (var tone in tonesToCleanUp)
				{
					_logger.Debug(
						$"AudioEvaluator->CleanUpTones: Cleaning up tone {tone.DtmfTone.HighTone} that has been around for {DateTime.Now.Subtract(tone.TimeStamp).TotalMinutes}m");
					_finishedTones.Remove(tone);
				}
			}

			return tonesToCleanUp.Count;
		}

		private void CheckFinishedTonesForTriggers()
		{
			_logger.Debug($"AudioEvaluator->CheckFinishedTonesForTriggers");

			if (_config.Watchers != null && _config.Watchers.Any() && _config.Watchers.Any(x => x.Active))
			{
				bool anyTones;
				lock (_lock)
					anyTones = _finishedTones.Count > 0;

				if (anyTones)
				{
					var activeWatchers = _config.Watchers.Where(x => x.Active).ToList();

					_logger.Debug($"AudioEvaluator->CheckFinishedTonesForTriggers: Active Watchers {activeWatchers.Count}");

					foreach (var watcher in activeWatchers)
					{
						_logger.Debug($"AudioEvaluator->CheckFinishedTonesForTriggers: Check Watcher {watcher.Name} triggers");

						bool alreadyActive;
						lock (_lock)
							alreadyActive = _startedWatchers.Contains(watcher.Id);

						if (!alreadyActive)
						{
							_logger.Debug($"AudioEvaluator->CheckFinishedTonesForTriggers: Watcher {watcher.Name} is not currently active in this tone bank, checking tones.");

							lock (_lock)
							{
								List<Tuple<Trigger, List<DtmfToneEnd>>> triggers = watcher.DidTriggerProcess(_finishedTones);

								if (triggers != null && triggers.Any())
								{
									var tones = triggers.SelectMany(x => x.Item2).Distinct().ToList();
									_logger.Debug($"AudioEvaluator->CheckFinishedTonesForTriggers: Watcher {watcher.Name} had {tones.Count} triggers match");

									_finishedTones.RemoveAll(x => tones.Select(y => y.Id).ToList().Contains(x.Id));

									WatcherTriggered?.Invoke(this,
										new WatcherEventArgs(watcher, triggers.Select(x => x.Item1).ToList(), tones.Select(x => x.DtmfTone).ToList(),
											DateTime.UtcNow));
								}
							}
						}
						else
						{
							_logger.Debug($"AudioEvaluator->CheckFinishedTonesForTriggers: Watcher {watcher.Name} has already been marked for this tone bank, not checking again.");
						}
					}
				}
			}
			else
			{
				lock (_lock)
				{
					_finishedTones.Clear();
				}
			}
		}
	}
}
