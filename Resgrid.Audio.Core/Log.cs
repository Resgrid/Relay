using System;
using System.IO;

namespace Resgrid.Audio.Core
{
	public sealed class Log : IDisposable
	{
		private readonly StreamWriter _file;

		public Log(string fileName)
		{
			_file = new StreamWriter(fileName, false);
		}

		public void Add(string message)
		{
			Console.WriteLine(message);
			_file.WriteLine(message);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		private void Dispose(bool disposing)
		{
			if (!disposing)
				return;

			_file.Flush();
			_file.Close();
		}
	}
}
