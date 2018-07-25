using Resgrid.Audio.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using DtmfDetection;
using DtmfDetection.NAudio;
using NAudio.Wave;
using Resgrid.Audio.Core.Events;
using Serilog.Core;

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
	}

	public class AudioEvaluator: IAudioEvaluator
	{
		private readonly Logger _logger;

		private Config _config;
		private List<DtmfTone> _dtmfTone;
		private static List<DtmfToneEnd> _finishedTones;

		public event EventHandler<EvaluatorEventArgs> EvaluatorStarted;
		public event EventHandler<EvaluatorEventArgs> EvaluatorFinished;
		public event EventHandler<WatcherEventArgs> WatcherTriggered;

		public AudioEvaluator(Logger logger)
		{
			_logger = logger;

			_dtmfTone = new List<DtmfTone>();
			_finishedTones = new List<DtmfToneEnd>();
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
									var addedTone = AddTone((int) trigger.Frequency1);
									trigger.Tones.Add(addedTone);
								}

								if (trigger.Count >= 2)
								{
									var addedTone = AddTone((int)trigger.Frequency2);
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
			_logger.Debug("AudioEvaluator->Clearning Tones");

			_finishedTones.Clear();
		}

		public void Start(IWaveIn waveIn)
		{
			var config = new DetectorConfig();
			config.PowerThreshold = _config.Tolerance;

			var analyzer = new LiveAudioDtmfAnalyzer(config, waveIn);

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

				if (end.Duration > new TimeSpan(0, 0, 0, 0, 0))
				{
					_logger.Debug($"AudioEvaluator->Finished Tones: {_finishedTones.Count}");

					foreach (var tone in _finishedTones)
					{
						_logger.Debug($"AudioEvaluator->Finished Tone: {tone.ToString()}");
					}

					var existingTone = _finishedTones.FirstOrDefault(x => x.DtmfTone.HighTone == end.DtmfTone.HighTone &&
					                   (end.TimeStamp.Subtract(x.TimeStamp).TotalMilliseconds <= 250 || end.TimeStamp.Subtract(x.TimeStamp).TotalMilliseconds >= -250));

					if (existingTone != null)
					{
						existingTone.Duration += end.Duration;
					}
					else
					{
						_finishedTones.Add(end);
					}
				}

				CheckFinishedTonesForTriggers();
			};

			analyzer.StartCapturing();
		}

		private DtmfTone AddTone(int frequency)
		{
			PureTones.TryAddHighTone(frequency);

			var tone = _dtmfTone.FirstOrDefault(x => x.HighTone == frequency);

			if (tone == null || tone == DtmfTone.None)
			{
				tone = new DtmfTone(frequency, 0, (PhoneKey)(_dtmfTone.Count() + (int)PhoneKey.Custom1));
				DtmfClassification.AddCustomTone(tone);
				_dtmfTone.Add(tone);
			}

			return tone;
		}

		private void CheckFinishedTonesForTriggers()
		{
			if (_config.Watchers != null && _config.Watchers.Any() && _config.Watchers.Any(x => x.Active))
			{
				if (_finishedTones != null && _finishedTones.Any())
				{
					var activeWatchers = _config.Watchers.Where(x => x.Active).ToList();

					foreach (var watcher in activeWatchers)
					{
						List<Tuple<Trigger, List<DtmfToneEnd>>> triggers = watcher.DidTriggerProcess(_finishedTones);

						if (triggers != null && triggers.Any())
						{
							var tones = triggers.SelectMany(x => x.Item2).Distinct().ToList();
							_finishedTones.RemoveAll(x => tones.Select(y => y.Id).ToList().Contains(x.Id));

							WatcherTriggered?.Invoke(this, new WatcherEventArgs(watcher, triggers.Select(x => x.Item1).ToList(), tones.Select(x => x.DtmfTone).ToList(), DateTime.UtcNow));
						}
					}
				}
			}
			else
			{
				_finishedTones.Clear();
			}
		}
	}
}
