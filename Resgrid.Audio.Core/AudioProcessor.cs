using System.Collections.Generic;

namespace Resgrid.Audio.Core
{
	public class AudioProcessor: IAudioProcessor
	{
		private Queue<byte> _buffer;

		private readonly IAudioRecorder _audioRecorder;
		private readonly IAudioEvaluator _audioEvaluator;

		public AudioProcessor(IAudioRecorder audioRecorder, IAudioEvaluator audioEvaluator)
		{
			_audioRecorder = audioRecorder;
			_audioEvaluator = audioEvaluator;

			_buffer = new Queue<byte>(26400000);	// 5 Minute Buffer, 1 second = 88,000 array elements
		}

		
	}
}
