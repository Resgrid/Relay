using System.Reflection;
using Consolas.Core;
using Resgrid.Audio.Relay.Console.Args;
using Resgrid.Audio.Relay.Console.Models;

namespace Resgrid.Audio.Relay.Console.Commands
{
	public class VersionCommand : Command
	{
		public object Execute(VersionArgs args)
		{
			var model = new VersionViewModel
			{
				Version = Assembly.GetExecutingAssembly().GetName().Version.ToString()
			};

			return View("Version", model);
		}
	}
}
