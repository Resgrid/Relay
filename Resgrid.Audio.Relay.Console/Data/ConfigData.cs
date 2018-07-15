using System;
using System.Collections.Generic;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Relay.Console.Data
{
	public class ConfigData
	{
		public static Config GetTestConfig()
		{
			Config config = new Config();
			config.ApiUrl = "https://api.resgrid.com";
			config.Username = "TEST";
			config.Password = "TEST";
			config.InputDevice = 0;

			config.Watchers = new List<Watcher>();
			var watcher1 = new Watcher();
			watcher1.Id = Guid.NewGuid();
			watcher1.Active = true;
			watcher1.Name = "Station 2";
			watcher1.Triggers = new List<Trigger>();

			watcher1.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 524,
				Frequency2 = 794
			});

			config.Watchers.Add(watcher1);

			var watcher2 = new Watcher();
			watcher2.Id = Guid.NewGuid();
			watcher2.Active = true;
			watcher2.Name = "Station 3";
			watcher2.Triggers = new List<Trigger>();
			watcher2.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 1084,
				Frequency2 = 794
			});

			config.Watchers.Add(watcher2);

			var watcher3 = new Watcher();
			watcher3.Id = Guid.NewGuid();
			watcher3.Active = true;
			watcher3.Name = "Station 5";
			watcher3.Triggers = new List<Trigger>();
			watcher3.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 881,
				Frequency2 = 1084
			});

			config.Watchers.Add(watcher3);

			var watcher4 = new Watcher();
			watcher4.Id = Guid.NewGuid();
			watcher4.Active = true;
			watcher4.Name = "Station 6";
			watcher4.Triggers = new List<Trigger>();
			watcher4.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 645,
				Frequency2 = 716
			});

			config.Watchers.Add(watcher4);

			var watcher5 = new Watcher();
			watcher5.Id = Guid.NewGuid();
			watcher5.Active = true;
			watcher5.Name = "Station 7";
			watcher5.Triggers = new List<Trigger>();
			watcher5.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 384,
				Frequency2 = 881
			});

			config.Watchers.Add(watcher5);

			var watcher6 = new Watcher();
			watcher6.Id = Guid.NewGuid();
			watcher6.Active = true;
			watcher6.Name = "Station 8";
			watcher6.Triggers = new List<Trigger>();
			watcher6.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 346,
				Frequency2 = 384
			});

			config.Watchers.Add(watcher6);

			var watcher7 = new Watcher();
			watcher7.Id = Guid.NewGuid();
			watcher7.Active = true;
			watcher7.Name = "Station 9";
			watcher7.Triggers = new List<Trigger>();
			watcher7.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 881,
				Frequency2 = 346
			});

			config.Watchers.Add(watcher7);

			var watcher8 = new Watcher();
			watcher8.Id = Guid.NewGuid();
			watcher8.Active = true;
			watcher8.Name = "Station 10";
			watcher8.Triggers = new List<Trigger>();
			watcher8.Triggers.Add(new Trigger
			{
				Count = 2,
				Frequency1 = 426,
				Frequency2 = 582
			});

			config.Watchers.Add(watcher8);

			return config;
		}
	}
}
