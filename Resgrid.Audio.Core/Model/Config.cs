using System.Collections.Generic;

namespace Resgrid.Audio.Core.Model
{
	public class Config
	{
		public int InputDevice { get; set; }
		public int AudioLength { get; set; }
		public string ApiUrl { get; set; }
		public string Username { get; set; }
		public string Password { get; set; }
		public bool Multiple { get; set; }
		public double Tolerance { get; set; }
		public sbyte Threshold { get; set; }
		public bool EnableSilenceDetection { get; set; }
		public bool Debug { get; set; }
		public List<Watcher> Watchers { get; set; }
	}
}
