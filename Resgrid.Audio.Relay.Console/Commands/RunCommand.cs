using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Consolas.Core;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
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
		private static IWatcherAudioStorage audioStorage;
		private static AudioRecorder recorder;
		private static AudioEvaluator evaluator;
		private static AudioProcessor processor;
		private static ComService com;

		public string Execute(RunArgs args)
		{
			CreateAudioDirectory();

			System.Console.WriteLine("Resgrid Audio");
			System.Console.WriteLine("-----------------------------------------");

			System.Console.WriteLine("Loading Settings");
			Config config = LoadSettingsFromFile();

			TelemetryConfiguration configuration = null;
			TelemetryClient telemetryClient = null;

			if (!String.IsNullOrWhiteSpace(config.DebugKey))
			{
				try
				{
					configuration = TelemetryConfiguration.Active;
					configuration.InstrumentationKey = config.DebugKey;
					configuration.TelemetryInitializers.Add(new OperationCorrelationTelemetryInitializer());
					configuration.TelemetryInitializers.Add(new HttpDependenciesParsingTelemetryInitializer());
					telemetryClient = new TelemetryClient();

					System.Console.WriteLine("Application Insights Debug Key Detected and AppInsights Initalized");
				}
				catch { }
			}

			using (InitializeDependencyTracking(configuration))
			{
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

				audioStorage = new WatcherAudioStorage(log);
				evaluator = new AudioEvaluator(log);
				recorder = new AudioRecorder(evaluator, audioStorage);
				processor = new AudioProcessor(recorder, evaluator, audioStorage);
				com = new ComService(log, processor);
				com.CallCreatedEvent += Com_CallCreatedEvent;

				System.Console.WriteLine("Hooking into Events");
				recorder.SampleAggregator.MaximumCalculated += SampleAggregator_MaximumCalculated;
				recorder.SampleAggregator.WaveformCalculated += SampleAggregator_WaveformCalculated;

				processor.TriggerProcessingStarted += Processor_TriggerProcessingStarted;
				processor.TriggerProcessingFinished += Processor_TriggerProcessingFinished;

				evaluator.WatcherTriggered += Evaluator_WatcherTriggered;

				ResgridV3ApiClient.Init(config.ApiUrl, config.Username, config.Password);

				System.Console.WriteLine(
					$"Config Loaded with {config.Watchers.Count} watchers ({config.Watchers.Count(x => x.Active)} active)");

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
					System.Console.WriteLine(
						"Communiation Service: CANNOT TALK TO RESGRID API, CHECK YOUR CONFIG APIURL AND ENSURE YOUR COMPUTER CAN TALK TO THAT URL");

				System.Console.WriteLine("Ready, Listening to Audio. Press Ctrl+C to exit.");

				while (recorder.RecordingState == RecordingState.Monitoring || recorder.RecordingState == RecordingState.Recording)
				{
					Thread.Sleep(250);
				}
			}

			if (telemetryClient != null)
			{
				telemetryClient.Flush();
				Task.Delay(5000).Wait();
			}

			return "";
		}

		private void Com_CallCreatedEvent(object sender, Core.Events.CallCreatedEventArgs e)
		{
			System.Console.WriteLine($"{e.Timestamp.ToString("G")}: CALL CREATED: {e.CallId} ({e.CallNumber})");
		}

		private static Config LoadSettingsFromFile()
		{
			var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			Config config = JsonConvert.DeserializeObject<Config>(File.ReadAllText($"{path}\\settings.json"));

			return config;
		}

		private static void CreateAudioDirectory()
		{
			var path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			Directory.CreateDirectory($"{path}\\DispatchAudio\\");
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
			//options.Columns = new[] { "Time1", "Max", "Min" };
			//options.EnableCount = false;

			//var table = new ConsoleTable(options);
			//table.AddRow(DateTime.Now.ToString("G"), e.MaxSample, e.MinSample);

			//table.Write();
		}

		private static DependencyTrackingTelemetryModule InitializeDependencyTracking(TelemetryConfiguration configuration)
		{
			var module = new DependencyTrackingTelemetryModule();

			if (configuration != null)
			{
				// prevent Correlation Id to be sent to certain endpoints. You may add other domains as needed.
				module.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.windows.net");
				module.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.chinacloudapi.cn");
				module.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.cloudapi.de");
				module.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.usgovcloudapi.net");
				module.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("localhost");
				module.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("127.0.0.1");

				// enable known dependency tracking, note that in future versions, we will extend this list. 
				// please check default settings in https://github.com/Microsoft/ApplicationInsights-dotnet-server/blob/develop/Src/DependencyCollector/NuGet/ApplicationInsights.config.install.xdt#L20
				module.IncludeDiagnosticSourceActivities.Add("Microsoft.Azure.ServiceBus");
				module.IncludeDiagnosticSourceActivities.Add("Microsoft.Azure.EventHubs");

				// initialize the module
				module.Initialize(configuration);
			}

			return module;
		}
	}
}
