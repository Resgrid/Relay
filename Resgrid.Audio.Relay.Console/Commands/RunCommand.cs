using System;
using System.IO;
using System.Linq;
using System.Threading;
using Consolas.Core;
using Newtonsoft.Json;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using Resgrid.Audio.Relay.Console.Args;
using Resgrid.Providers.ApiClient.V3;
using Serilog;
using Serilog.Core;

namespace Resgrid.Audio.Relay.Console.Commands
{
	public class RunCommand : Command
	{
		private static AudioRecorder recorder;
		private static AudioEvaluator evaluator;
		private static AudioProcessor processor;
		private static ComService com;

		public string Execute(RunArgs args)
		{
			System.Console.WriteLine("Resgrid Audio");
			System.Console.WriteLine("-----------------------------------------");

			System.Console.WriteLine("Loading Settings");
			Config config = LoadSettingsFromFile();

			System.Console.WriteLine($"Listening for Dispatches on Device: {config.InputDevice}");

			Logger log;

			if (config.Debug)
			{
				log = new LoggerConfiguration()
					.MinimumLevel.Debug()
					.WriteTo.Console()
					.CreateLogger();
			}
			else
			{
				log = new LoggerConfiguration()
					.MinimumLevel.Error()
					.WriteTo.Console()
					.CreateLogger();
			}

			evaluator = new AudioEvaluator(log);
			recorder = new AudioRecorder(evaluator);
			processor = new AudioProcessor(recorder, evaluator);
			com = new ComService(processor);


			System.Console.WriteLine("Hooking into Events");
			recorder.SampleAggregator.MaximumCalculated += SampleAggregator_MaximumCalculated;
			recorder.SampleAggregator.WaveformCalculated += SampleAggregator_WaveformCalculated;
			
			processor.TriggerProcessingStarted += Processor_TriggerProcessingStarted;
			processor.TriggerProcessingFinished += Processor_TriggerProcessingFinished;

			evaluator.WatcherTriggered += Evaluator_WatcherTriggered;

			ResgridV3ApiClient.Init(config.ApiUrl, config.Username, config.Password);

			System.Console.WriteLine($"Config Loaded with {config.Watchers.Count} watchers ({config.Watchers.Select(x => x.Active).Count()} active)");

			System.Console.WriteLine("Initializing Processor");
			processor.Init(config);

			System.Console.WriteLine("Starting Processor");
			processor.Start();

			System.Console.WriteLine("Starting Communiation Service");
			com.Init(config);
			System.Console.WriteLine("Communiation Service: Validating API Connection");

			if (com.IsConnectionValid())
				System.Console.WriteLine("Communiation Service: API Connection is Valid");
			else
				System.Console.WriteLine("Communiation Service: CANNOT TALK TO RESGRID API, CHECK YOUR CONFIGS APIURL AND ENSURE YOUR COMPUTER CAN TALK TO THAT URL");

			System.Console.WriteLine("Ready, Listening to Audio. Press Ctrl+C to exit.");

			while (recorder.RecordingState == RecordingState.Monitoring || recorder.RecordingState == RecordingState.Recording)
			{
				Thread.Sleep(250);
			}

			return "";
		}

		private static Config LoadSettingsFromFile()
		{
			var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			Config config = JsonConvert.DeserializeObject<Config>(File.ReadAllText($"{path}\\settings.json"));

			return config;
		}

		private static void Evaluator_WatcherTriggered(object sender, Core.Events.WatcherEventArgs e)
		{
			System.Console.WriteLine($"{DateTime.Now.ToString("G")}: WATCHER TRIGGERED: {e.Watcher.Name}");
		}

		private static void Processor_TriggerProcessingFinished(object sender, Core.Events.TriggerProcessedEventArgs e)
		{
			//System.Console.WriteLine($"TRIGGER FINISHED: {e.Watcher.Name}");
			//var path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase);

			//System.IO.File.WriteAllBytes(path + $"\\{DateTime.Now.Month}-{DateTime.Now.Day}-{DateTime.Now.Year}_{DateTime.Now.Hour}:{DateTime.Now.Minute}:{DateTime.Now.Second}.wav", e.Watcher.GetBuffer());
		}

		private static void Processor_TriggerProcessingStarted(object sender, Core.Events.TriggerProcessedEventArgs e)
		{
			//System.Console.WriteLine($"TRIGGER STARTED: {e.Watcher.Name}");
		}

		private static void SampleAggregator_WaveformCalculated(object sender, WaveformEventArgs e)
		{

		}

		private static void SampleAggregator_MaximumCalculated(object sender, MaxSampleEventArgs e)
		{
			//ConsoleTableOptions options = new ConsoleTableOptions();
			//options.Columns = new[] { "Time", "Max", "Min" };
			//options.EnableCount = false;

			//var table = new ConsoleTable(options);
			//table.AddRow(DateTime.Now.ToString("G"), e.MaxSample, e.MinSample);

			//table.Write();
		}
	}
}
