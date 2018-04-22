using System.Collections.Generic;

namespace Resgrid.Audio.Core.Model
{
	public class Watcher
	{
		public string Name { get; set; }
		public bool Active { get; set; }
		public int Type { get; set; }
		public int Eval { get; set; }
		public List<Trigger> Triggers { get; set; }
	}
}
