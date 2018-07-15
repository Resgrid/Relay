using System;
using System.Net.Http;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public class ComService
	{
		private AudioProcessor _audioProcessor;
		private Config _config;

		public ComService(AudioProcessor audioProcessor)
		{
			_audioProcessor = audioProcessor;
		}

		public void Init(Config config)
		{
			_config = config;

			_audioProcessor.TriggerProcessingFinished += _audioProcessor_TriggerProcessingFinished;
		}

		private void _audioProcessor_TriggerProcessingFinished(object sender, Events.TriggerProcessedEventArgs e)
		{
			
		}
	}
}
