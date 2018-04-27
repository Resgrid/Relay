using System.Threading;

namespace Resgrid.Audio.Relay.Console.Models
{
	public interface IThreadService
	{
		void Sleep(int millisecondsTimeout);
	}

	public class ThreadService : IThreadService
	{
		public void Sleep(int millisecondsTimeout)
		{
			Thread.Sleep(millisecondsTimeout);
		}
	}
}
