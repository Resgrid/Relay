using System;
using System.Threading;
using Consolas.Core;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;
using Resgrid.Audio.Relay.Console.Args;
using Serilog;
using Serilog.Core;

namespace Resgrid.Audio.Relay.Console.Commands
{
	public class MonitorCommand : Command
	{
		private static AudioRecorder recorder;
		private static IWatcherAudioStorage audioStorage;
		private static AudioEvaluator evaluator;
		private static AudioProcessor processor;

		public string Execute(MonitorArgs args)
		{
			Logger log = new LoggerConfiguration()
				.MinimumLevel.Error()
				.WriteTo.Console()
				.CreateLogger();

			audioStorage = new WatcherAudioStorage();
			evaluator = new AudioEvaluator(log);
			recorder = new AudioRecorder(evaluator, audioStorage);
			processor = new AudioProcessor(recorder, evaluator, audioStorage);

			System.Console.WriteLine("Resgrid Audio");
			System.Console.WriteLine("-----------------------------------------");
			System.Console.WriteLine("Monitoring Audio on Device: " + args.Device);

			System.Console.WriteLine("Hooking into Events");
			recorder.SampleAggregator.MaximumCalculated += SampleAggregator_MaximumCalculated;
			recorder.SampleAggregator.WaveformCalculated += SampleAggregator_WaveformCalculated;
			
			processor.TriggerProcessingStarted += Processor_TriggerProcessingStarted;
			processor.TriggerProcessingFinished += Processor_TriggerProcessingFinished;

			evaluator.WatcherTriggered += Evaluator_WatcherTriggered;

			System.Console.WriteLine("Loading Settings");
			Config config = new Config();
			config.InputDevice = args.Device;
			config.AudioLength = 10;

			System.Console.WriteLine("Initializing Processor");
			processor.Init(config);

			System.Console.WriteLine("Starting Processor");
			processor.Start();

			System.Console.WriteLine("Ready, Monitoring Audio. Press Ctrl+C to exit.");
			System.Console.WriteLine($"Timestamp:		Min			Max			dB");
			System.Console.WriteLine($"---------------------------------------------------------------------------");

			while (recorder.RecordingState == RecordingState.Monitoring || recorder.RecordingState == RecordingState.Recording)
			{
				Thread.Sleep(100);
			}

			return "";
		}

		private static void Evaluator_WatcherTriggered(object sender, Core.Events.WatcherEventArgs e)
		{
			//System.Console.WriteLine($"WATCHER TRIGGERED: {e.Watcher.Name}");
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
			//options.Columns = new[] { "Time1", "Max", "Min", "Db" };
			//options.EnableCount = false;

			//var table = new ConsoleTable(options);
			//table.AddRow(DateTime.Now.ToString("G"), e.MaxSample, e.MinSample, e.Db);

			//table.Write();

			System.Console.WriteLine($"{DateTime.Now.ToString("G")}:	{e.MinSample}		{e.MaxSample}		{e.Db}");
		}
	}
}
