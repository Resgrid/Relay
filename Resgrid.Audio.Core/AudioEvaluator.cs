using Resgrid.Audio.Core.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using DtmfDetection;
using DtmfDetection.NAudio;
using NAudio.Wave;
using Resgrid.Audio.Core.Events;

namespace Resgrid.Audio.Core
{
	public class AudioEvaluator: IAudioEvaluator
	{
		private List<Watcher> _watchers;
		private List<DtmfTone> _dtmfTone;
		private static List<DtmfTone> _finishedTones;

		public event EventHandler<EvaluatorEventArgs> EvaluatorStarted;
		public event EventHandler<EvaluatorEventArgs> EvaluatorFinished;
		public event EventHandler<WatcherEventArgs> WatcherTriggered;

		public AudioEvaluator()
		{
			_watchers = new List<Watcher>();
			_dtmfTone = new List<DtmfTone>();
			_finishedTones = new List<DtmfTone>();
		}

		public void Init(List<Watcher> watchers)
		{
			_watchers = watchers;

			if (_watchers != null && _watchers.Any())
			{
				PureTones.ClearTones();
				DtmfClassification.ClearAllTones();

				foreach (var watcher in watchers)
				{ 
					if (watcher.Triggers != null && watcher.Triggers.Any())
					{
						foreach (var trigger in watcher.Triggers)
						{
							if (trigger != null)
							{
								if (trigger.Count >= 1)
								{
									trigger.Tones.Add(AddTone((int) trigger.Frequency1));
								}

								if (trigger.Count >= 2)
								{
									trigger.Tones.Add(AddTone((int) trigger.Frequency1));
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

		public void Start(IWaveIn waveIn)
		{
			var analyzer = new LiveAudioDtmfAnalyzer(waveIn);

			//analyzer.DtmfToneStarted += start => _log.Add($"{start.DtmfTone.Key} key started on {start.Position.TimeOfDay} (channel {start.Channel})");
			//analyzer.DtmfToneStopped += end => _log.Add($"{end.DtmfTone.Key} key stopped after {end.Duration.TotalSeconds}s (channel {end.Channel})");

			analyzer.DtmfToneStarted += start =>
			{
				EvaluatorStarted?.Invoke(this, new EvaluatorEventArgs(start, null, DateTime.UtcNow));
			};

			analyzer.DtmfToneStopped += end =>
			{
				EvaluatorFinished?.Invoke(this, new EvaluatorEventArgs(null, end, DateTime.UtcNow));

				_finishedTones.Add(end.DtmfTone);
				CheckFinishedTonesForTriggers();
			};
		}

		private DtmfTone AddTone(int frequency)
		{
			PureTones.TryAddHighTone(frequency);

			var tone = _dtmfTone.FirstOrDefault(x => x.HighTone == frequency);

			if (tone == null)
			{
				tone = new DtmfTone(frequency, 0, (PhoneKey)(_dtmfTone.Count() + (int)PhoneKey.Custom1));
				_dtmfTone.Add((tone));
			}

			return tone;
		}

		private void CheckFinishedTonesForTriggers()
		{
			if (_watchers != null && _watchers.Any())
			{
				if (_finishedTones != null && _finishedTones.Any())
				{
					foreach (var watcher in _watchers)
					{
						List<Tuple<Trigger, List<DtmfTone>>> triggers = watcher.DidTriggerProcess(_finishedTones);

						if (triggers != null && triggers.Any())
						{
							var tones = triggers.SelectMany(x => x.Item2).Distinct().ToList();

							foreach (var tone in tones)
							{
								_finishedTones.Remove(tone);
							}

							WatcherTriggered?.Invoke(this, new WatcherEventArgs(watcher, triggers.Select(x => x.Item1).ToList(), tones, DateTime.UtcNow));
						}
					}
				}
			}
		}
	}
}
