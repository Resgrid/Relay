using System.Collections.Generic;
using System.Linq;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public class AudioProcessor: IAudioProcessor
	{
		private const int BUFFER_SIZE = 26400000; // 5 Minute Buffer, 1 second = 88,000 array elements
		private Queue<byte> _buffer;
		private Settings _settings;

		private readonly IAudioRecorder _audioRecorder;
		private readonly IAudioEvaluator _audioEvaluator;
		private readonly SampleAggregator _sampleAggregator;

		public AudioProcessor(IAudioRecorder audioRecorder, IAudioEvaluator audioEvaluator)
		{
			_audioRecorder = audioRecorder;
			_audioEvaluator = audioEvaluator;

			_buffer = new Queue<byte>(BUFFER_SIZE);
			_sampleAggregator = new SampleAggregator();
		}

		public void Init()
		{
			_sampleAggregator.WaveformCalculated += _sampleAggregator_WaveformCalculated;
			_sampleAggregator.DataAvailable += _sampleAggregator_DataAvailable;
		}

		private void _sampleAggregator_DataAvailable(object sender, DataAvailableArgs e)
		{
			if (e?.Buffer != null)
			{
				foreach (var buffer in e.Buffer)
				{
					_buffer.Enqueue(buffer);
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
							if (_audioEvaluator.EvaluateAudioTrigger(trigger, e.FastFourierTransform))
							{

							}
						}
					}
				}
			}
		}
	}
}
