using Consolas.Core;
using NAudio.Wave;
using Resgrid.Audio.Core.Model;
using Resgrid.Audio.Relay.Console.Args;
using System.Collections.Generic;

namespace Resgrid.Audio.Relay.Console.Commands
{
	public class SetupCommand : Command
	{
		public string Execute(HelpArgs args)
		{
			System.Console.WriteLine("Resgrid Audio Setup");
			System.Console.WriteLine("-----------------------------------------");
			System.Console.WriteLine("Please answer the following question to get the Resgrid relay appliaction setup.");
			System.Console.WriteLine("You can always manually change values by editing the settings.json file in a text editor");
			System.Console.WriteLine("");

			System.Console.WriteLine("Please enter your Resgrid username:");
			var userName = System.Console.ReadLine();

			System.Console.WriteLine("Please enter your Resgrid password:");
			var password = System.Console.ReadLine();

			System.Console.WriteLine("Which audio device do you want Relay to monitor?");
			int waveInDevices = WaveIn.DeviceCount;
			for (int waveInDevice = 0; waveInDevice < waveInDevices; waveInDevice++)
			{
				WaveInCapabilities deviceInfo = WaveIn.GetCapabilities(waveInDevice);
				System.Console.WriteLine($"Device {waveInDevice}: {deviceInfo.ProductName}, {deviceInfo.Channels} channels");
			}
			var audioDevice = System.Console.ReadLine();

			System.Console.WriteLine("How long, in seconds, do you want Relay to record before dispatching?");
			var audioLength = System.Console.ReadLine();

			System.Console.WriteLine("How many groups (or your department) do you want to dispach?");
			var watcherCount = System.Console.ReadLine();
			int watcherCountInt = int.Parse(watcherCount);

			List<Watcher> watchers = new List<Watcher>();
			int i = 0;
			while (i < watcherCountInt)
			{
				Watcher watcher = SetupWatcher(i);

				if (watcher != null)
				{
					i++;
				}
			}

			return "";
		}

		private Watcher SetupWatcher(int count)
		{
			System.Console.WriteLine($"Options for #{count + 1}");
			System.Console.WriteLine($"=========================");
			var watcher = new Watcher();

			System.Console.WriteLine("Watcher Name (i.e. Station 1):");
			var watcherName = System.Console.ReadLine();

			System.Console.WriteLine("Watcher Group or Department Dispatch Code:");
			var dispatchCode = System.Console.ReadLine();

			System.Console.WriteLine("Is this a department code (y/n):");
			var department = System.Console.ReadLine();
			int type = 1;

			if (!department.ToLower().Contains("y"))
			{
				type = 2;
				System.Console.WriteLine("Additional Dispatch codes seperated by commas:");
				var additionalCodes = System.Console.ReadLine();
			}

			var triggers = ProcessTriggers(1);

			return null;
		}

		private List<Trigger> ProcessTriggers(int count)
		{
			var triggers = new List<Trigger>();

			System.Console.WriteLine("How many frequences (tones) for this watcher (1 or 2):");
			var frequencyCount = System.Console.ReadLine();
			int frequencyCountInt = int.Parse(frequencyCount);


			return triggers;
		}
	}
}
