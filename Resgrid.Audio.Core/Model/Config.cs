﻿using System.Collections.Generic;

namespace Resgrid.Audio.Core.Model
{
	public class Config
	{
		public int InputDevice { get; set; }
		public int AudioLength { get; set; }
		public string ApiUrl { get; set; }
		public string Username { get; set; }
		public string Password { get; set; }
		public List<Watcher> Watchers { get; set; }
	}
}
