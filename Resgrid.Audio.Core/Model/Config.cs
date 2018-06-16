using System.Collections.Generic;

namespace Resgrid.Audio.Core.Model
{
	public class Config
	{
		public int InputDevice { get; set; }
		public string ApiUrl { get; set; }
		public string ApiCode { get; set; }
		public List<Watcher> Watchers { get; set; }
	}
}
