using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Resgrid.Audio.Relay.ViewModel
{
	public class ShuttingDownMessage
	{
		public string CurrentViewName { get; private set; }
		public ShuttingDownMessage(string currentViewName)
		{
			this.CurrentViewName = currentViewName;
		}
	}
}
