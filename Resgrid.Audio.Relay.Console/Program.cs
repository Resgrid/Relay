using System;
using System.Threading;
using Consolas.Core;
using Consolas.Mustache;
using Resgrid.Audio.Core;
using Resgrid.Audio.Relay.Console.Models;
using SimpleInjector;

namespace Resgrid.Audio.Relay.Console
{
	public class Program : ConsoleApp<Program>
	{
		private static AudioRecorder recorder;
		private static AudioEvaluator evaluator;
		private static AudioProcessor processor;

		static void Main(string[] args)
		{
			Match(args);

			recorder = new AudioRecorder();
			evaluator = new AudioEvaluator();
			processor = new AudioProcessor(recorder, evaluator);

			recorder.SampleAggregator.MaximumCalculated += SampleAggregator_MaximumCalculated;
			recorder.SampleAggregator.WaveformCalculated += SampleAggregator_WaveformCalculated;

			processor.TriggerProcessingStarted += Processor_TriggerProcessingStarted;
			processor.TriggerProcessingFinished += Processor_TriggerProcessingFinished;

			processor.Start();

			while (recorder.RecordingState == RecordingState.Monitoring || recorder.RecordingState == RecordingState.Recording)
			{
				Thread.Sleep(250);
			}
		}

		private static void Processor_TriggerProcessingFinished(object sender, Core.Events.TriggerProcessedEventArgs e)
		{
			System.Console.WriteLine($"TRIGGER FINISHED: {e.Watcher.Name}");
			var path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase);

			System.IO.File.WriteAllBytes(path + $"\\{DateTime.Now.Month}-{DateTime.Now.Day}-{DateTime.Now.Year}_{DateTime.Now.Hour}:{DateTime.Now.Minute}:{DateTime.Now.Second}.wav", e.Watcher.GetBuffer());
		}

		private static void Processor_TriggerProcessingStarted(object sender, Core.Events.TriggerProcessedEventArgs e)
		{
			System.Console.WriteLine($"TRIGGER STARTED: {e.Watcher.Name}");
		}

		private static void SampleAggregator_WaveformCalculated(object sender, WaveformEventArgs e)
		{
			
		}

		private static void SampleAggregator_MaximumCalculated(object sender, MaxSampleEventArgs e)
		{
			ConsoleTableOptions options = new ConsoleTableOptions();
			options.Columns = new[] {"Time", "Max", "Min"};
			options.EnableCount = false;

			var table = new ConsoleTable(options);
			table.AddRow(DateTime.Now.ToString("G"), e.MaxSample, e.MinSample);

			table.Write();
		}

		public override void Configure(Container container)
		{
			container.Register<IConsole, SystemConsole>();
			container.Register<IThreadService, ThreadService>();


			ViewEngines.Add<MustacheViewEngine>();
		}
	}
}
