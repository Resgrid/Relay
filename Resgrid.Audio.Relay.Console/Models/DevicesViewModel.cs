using System.Collections.Generic;

namespace Resgrid.Audio.Relay.Console.Models
{
	public class DevicesViewModel
	{
		public List<string> Devices { get; set; }

		public DevicesViewModel()
		{
			Devices = new List<string>();
		}
	}
}
