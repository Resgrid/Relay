using Consolas.Core;
using NAudio.Wave;
using Newtonsoft.Json;
using Resgrid.Audio.Core.Model;
using Resgrid.Audio.Relay.Console.Args;
using System;
using System.Collections.Generic;
using System.IO;

namespace Resgrid.Audio.Relay.Console.Commands
{
	public class SetupCommand : Command
	{
		public string Execute(SetupArgs args)
		{
			System.Console.WriteLine("Resgrid Audio Setup");
			System.Console.WriteLine("-----------------------------------------");
			System.Console.WriteLine("Please answer the following question to get the Resgrid relay application setup.");
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

			System.Console.WriteLine("How many groups (or your department) do you want to dispatch?");
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
					watchers.Add(watcher);
				}
			}

			Config config = LoadSettingsFromFile();
			config.Username = userName;
			config.Password = password;
			config.InputDevice = int.Parse(audioDevice);
			config.AudioLength = int.Parse(audioLength);
			config.Watchers = watchers;

			SaveSettingsFromFile(config);

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

			string additionalCodes = "";
			if (!department.ToLower().Contains("y"))
			{
				type = 2;
				System.Console.WriteLine("Additional Dispatch codes separated by commas:");
				additionalCodes = System.Console.ReadLine();
			}

			var triggers = ProcessTriggers(1);

			watcher.Id = Guid.NewGuid();
			watcher.Active = true;
			watcher.Name = watcherName.Trim();
			watcher.Code = dispatchCode;
			watcher.Type = type;

			if (!String.IsNullOrWhiteSpace(additionalCodes))
				watcher.AdditionalCodes = additionalCodes;

			watcher.Triggers = triggers;

			return watcher;
		}

		private List<Trigger> ProcessTriggers(int count)
		{
			var triggers = new List<Trigger>();

			System.Console.WriteLine("How many frequencies (tones) for this watcher (1 or 2):");
			var frequencyCount = System.Console.ReadLine();
			int frequencyCountInt = int.Parse(frequencyCount);


			System.Console.WriteLine("What frequency is Tone 1 (whole numbers only):");
			var frequency1 = System.Console.ReadLine();
			int frequency1Int = int.Parse(frequency1);

			System.Console.WriteLine("How long does Tone 1 last (in milliseconds 1000 = 1 sec):");
			var frequency1Time = System.Console.ReadLine();
			int frequency1TimeInt = int.Parse(frequency1Time);

			var trigger = new Trigger();
			trigger.Count = frequencyCountInt;
			trigger.Frequency1 = frequency1Int;
			trigger.Time1 = frequency1TimeInt;

			string frequency2;
			int frequency2Int;
			string frequency2Time;
			int frequency2TimeInt;

			if (frequencyCountInt == 2)
			{
				System.Console.WriteLine("What frequency is Tone 2 (whole numbers only):");
				frequency2 = System.Console.ReadLine();
				frequency2Int = int.Parse(frequency2);

				System.Console.WriteLine("How long does Tone 2 last (in milliseconds 1000 = 1 sec):");
				frequency2Time = System.Console.ReadLine();
				frequency2TimeInt = int.Parse(frequency2Time);


				trigger.Frequency2 = frequency2Int;
				trigger.Time2 = frequency2TimeInt;
			}

			triggers.Add(trigger);

			return triggers;
		}

		private static Config LoadSettingsFromFile()
		{
			var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			Config config = JsonConvert.DeserializeObject<Config>(File.ReadAllText($"{path}\\settings.json"));

			return config;
		}

		private static void SaveSettingsFromFile(Config config)
		{
			var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			File.WriteAllText($"{path}\\settings.json", JsonConvert.SerializeObject(config));
		}
	}
}
