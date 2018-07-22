using Consolas.Core;
using Consolas.Mustache;
using Resgrid.Audio.Relay.Console.Models;
using SimpleInjector;

namespace Resgrid.Audio.Relay.Console
{
	public class Program : ConsoleApp<Program>
	{
		static void Main(string[] args)
		{
			Match(args);
		}

		public override void Configure(Container container)
		{
			container.Register<IConsole, SystemConsole>();
			container.Register<IThreadService, ThreadService>();


			ViewEngines.Add<MustacheViewEngine>();
		}
	}
}
