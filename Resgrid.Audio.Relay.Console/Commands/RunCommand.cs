using System;
using System.IO;
using System.Threading;
using Consolas.Core;
using Newtonsoft.Json;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using Resgrid.Audio.Relay.Console.Args;

namespace Resgrid.Audio.Relay.Console.Commands
{
	public class RunCommand : Command
	{
		private static AudioRecorder recorder;
		private static AudioEvaluator evaluator;
		private static AudioProcessor processor;

		public string Execute(RunArgs args)
		{
			recorder = new AudioRecorder();
			evaluator = new AudioEvaluator();
			processor = new AudioProcessor(recorder, evaluator);

			recorder.SampleAggregator.MaximumCalculated += SampleAggregator_MaximumCalculated;
			recorder.SampleAggregator.WaveformCalculated += SampleAggregator_WaveformCalculated;

			processor.TriggerProcessingStarted += Processor_TriggerProcessingStarted;
			processor.TriggerProcessingFinished += Processor_TriggerProcessingFinished;

			evaluator.WatcherTriggered += Evaluator_WatcherTriggered;

			processor.Init(LoadSettingsFromFile().Watchers);
			processor.Start();

			while (recorder.RecordingState == RecordingState.Monitoring || recorder.RecordingState == RecordingState.Recording)
			{
				Thread.Sleep(250);
			}

			return "Using: Resgrid.Audio.Relay.Console.exe ...";
		}

		private static Config LoadSettingsFromFile()
		{
			var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			Config config = JsonConvert.DeserializeObject<Config>(File.ReadAllText($"{path}\\settings.json"));

			return config;
		}

		private static void Evaluator_WatcherTriggered(object sender, Core.Events.WatcherEventArgs e)
		{
			System.Console.WriteLine($"WATCHER TRIGGERED: {e.Watcher.Name}");
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
			ConsoleTableOptions options = new ConsoleTableOptions();
			options.Columns = new[] { "Time", "Max", "Min" };
			options.EnableCount = false;

			var table = new ConsoleTable(options);
			table.AddRow(DateTime.Now.ToString("G"), e.MaxSample, e.MinSample);

			table.Write();
		}
	}
}
