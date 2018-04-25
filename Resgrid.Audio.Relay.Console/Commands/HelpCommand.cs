using Consolas.Core;
using Resgrid.Audio.Relay.Console.Args;

namespace Resgrid.Audio.Relay.Console.Commands
{
    public class HelpCommand : Command
    {
        public string Execute(HelpArgs args)
        {
            return "Using: Resgrid.Audio.Relay.Console.exe ...";
        }
    }
}