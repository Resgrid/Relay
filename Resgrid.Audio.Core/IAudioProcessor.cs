using System.Collections.Generic;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public interface IAudioProcessor
	{
		void Init(Config config);
		void Start();
	}
}
