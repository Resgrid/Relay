using System.Threading;
using Consolas.Core;
using Consolas.Mustache;
using Resgrid.Audio.Core;
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

			processor.Init();
			recorder.BeginMonitoring(0);

			while (recorder.RecordingState == RecordingState.Monitoring || recorder.RecordingState == RecordingState.Recording)
			{
				Thread.Sleep(250);
			}
		}

		private static void SampleAggregator_WaveformCalculated(object sender, WaveformEventArgs e)
		{
			
		}

		private static void SampleAggregator_MaximumCalculated(object sender, MaxSampleEventArgs e)
		{
			var table = new ConsoleTable("Time", "Max", "Min");
			table.AddRow(recorder.RecordedTime.ToString("g"), e.MaxSample, e.MinSample);

			table.Write();
			System.Console.WriteLine();
		}

		public override void Configure(Container container)
		{
			container.Register<IConsole, SystemConsole>();
			ViewEngines.Add<MustacheViewEngine>();
		}
	}
}
