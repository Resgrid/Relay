#if NET10_0_WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Core.Radio;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Audio.Voice;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Connection;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Audio.Voice.LiveKit;
using Resgrid.Audio.Voice.Recording;
using Resgrid.Providers.ApiClient.V4;
using Serilog;
using Cli = System.Console;

namespace Resgrid.Audio.Relay.Console.Voice
{
	/// <summary>
	/// 'radio' mode (Windows): bidirectional bridge between a physically-attached radio
	/// and a Resgrid PTT channel, with anti-static squelch, PTT keying, optional
	/// recording, and emergency/MDC-1200 detection.
	/// </summary>
	internal static class RadioMode
	{
		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken)
		{
			ResgridV4ApiClient.Init(options.Resgrid);

			var deptId = string.IsNullOrWhiteSpace(options.Voice.DepartmentId) ? null : options.Voice.DepartmentId;
			var transport = new LiveKitVoiceTransport(logger, options.Voice.PublishQueueMs);
			var provider = new ResgridVoiceChannelProvider(logger);

			if (options.Voice.EnforceSeatLimit)
			{
				var canConnect = await provider.CanConnectAsync(deptId, cancellationToken).ConfigureAwait(false);
				if (canConnect == false)
				{
					Cli.Error.WriteLine("Voice seat limit reached for this department; not connecting.");
					return 1;
				}
			}

			await using var manager = new VoiceRoomManager(transport, logger);
			var channel = await provider.GetChannelAsync(options.Voice.Channel, deptId, cancellationToken).ConfigureAwait(false);
			var session = await manager.JoinAsync(channel, cancellationToken).ConfigureAwait(false);

			var radioSettings = MapSettings(options.Radio);
			var device = new NAudioRadioDevice(radioSettings.InputDevice, radioSettings.OutputDevice, logger);
			using var ptt = CreatePtt(radioSettings, logger);
			using var carrier = CreateCarrier(radioSettings, logger);

			var (mdc, emergency, alertSink) = BuildSignaling(options.Radio.Emergency, logger);

			await using var bridge = new RadioBridge(device, ptt, carrier, radioSettings, logger, mdc, emergency, alertSink);
			await bridge.StartAsync(session, cancellationToken).ConfigureAwait(false);

			TransmissionRecorder recorder = null;
			List<IDisposable> recorderDisposables = null;
			ITransmissionLog recorderLog = null;
			if (options.Radio.RecordWhileBridging)
			{
				var (stores, disposables) = RecordMode.BuildStores(options.Recorder, logger);
				recorderDisposables = disposables;
				recorderLog = RecordMode.BuildLog(options.Recorder, logger);
				recorder = new TransmissionRecorder(session, options.Recorder.Segmentation, stores, recorderLog, logger);
				recorder.Start();
			}

			Cli.WriteLine($"Radio bridge running on '{channel.Name}'. Press Ctrl+C to stop.");
			await VoiceModeRuntime.WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);

			if (recorder != null)
				await recorder.DisposeAsync().ConfigureAwait(false);
			if (recorderLog != null)
				await recorderLog.DisposeAsync().ConfigureAwait(false);
			if (recorderDisposables != null)
				foreach (var d in recorderDisposables) d.Dispose();

			await bridge.DisposeAsync().ConfigureAwait(false);
			device.Dispose();
			return 0;
		}

		/// <summary>Live receive-level meter + squelch state to help tune the anti-static threshold.</summary>
		public static async Task<int> RunTuneAsync(RelayHostOptions options, CancellationToken cancellationToken)
		{
			var logger = Serilog.Core.Logger.None;
			var radioSettings = MapSettings(options.Radio);
			var gate = new SquelchGate(radioSettings.Squelch, AudioFormat.SampleRate);
			var device = new NAudioRadioDevice(radioSettings.InputDevice, radioSettings.OutputDevice, logger);

			var lastPrintTicks = 0L;
			device.SamplesReceived += (_, frame) =>
			{
				bool open = gate.Process(frame);
				var now = DateTime.UtcNow.Ticks;
				if (now - lastPrintTicks < TimeSpan.TicksPerMillisecond * 150)
					return;
				lastPrintTicks = now;

				double db = gate.LastDbfs;
				int bars = Math.Clamp((int)((db + 80) / 80 * 30), 0, 30);
				var meter = new string('#', bars).PadRight(30, '·');
				Cli.Write($"\r[{meter}] {db,6:0.0} dBFS  {(open ? "OPEN  " : "closed")}   ");
			};

			device.StartReceive();
			Cli.WriteLine($"Tuning input device {radioSettings.InputDevice}. Open={radioSettings.Squelch.OpenDbfs} dBFS, Close={radioSettings.Squelch.CloseDbfs} dBFS.");
			Cli.WriteLine("Key up the radio (or feed static) and adjust OpenDbfs just above the static floor. Ctrl+C to stop.");

			await VoiceModeRuntime.WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
			device.Dispose();
			Cli.WriteLine();
			return 0;
		}

		private static RadioSettings MapSettings(RadioModeOptions o)
		{
			Enum.TryParse<PttKeyingMethod>(o.PttMethod, true, out var ptt);
			Enum.TryParse<CarrierDetectSource>(o.CarrierDetect, true, out var carrier);

			return new RadioSettings
			{
				InputDevice = o.InputDevice,
				OutputDevice = o.OutputDevice,
				Ptt = ptt,
				SerialPort = o.SerialPort,
				Cm108VendorId = o.Cm108VendorId,
				Cm108ProductId = o.Cm108ProductId,
				Cm108GpioPin = o.Cm108GpioPin,
				CarrierDetect = carrier,
				CarrierDetectInverted = o.CarrierDetectInverted,
				CourtesyTone = o.CourtesyTone,
				TxHangMs = o.TxHangMs,
				TxTailMs = o.TxTailMs,
				AntiLoop = o.AntiLoop,
				TxGain = o.TxGain,
				RxGain = o.RxGain,
				HighPassCutoffHz = o.HighPassCutoffHz,
				Squelch = o.Squelch
			};
		}

		private static IPttController CreatePtt(RadioSettings settings, ILogger logger)
		{
			switch (settings.Ptt)
			{
				case PttKeyingMethod.SerialRts:
					return new SerialPttController(settings.SerialPort, useDtr: false, logger);
				case PttKeyingMethod.SerialDtr:
					return new SerialPttController(settings.SerialPort, useDtr: true, logger);
				case PttKeyingMethod.Cm108:
					return new Cm108PttController(settings.Cm108VendorId, settings.Cm108ProductId, settings.Cm108GpioPin, logger);
				case PttKeyingMethod.Vox:
				default:
					return new VoxPttController();
			}
		}

		private static ICarrierDetector CreateCarrier(RadioSettings settings, ILogger logger)
		{
			switch (settings.CarrierDetect)
			{
				case CarrierDetectSource.SerialCts:
					return new SerialCarrierDetector(settings.SerialPort, useDsr: false, settings.CarrierDetectInverted, logger);
				case CarrierDetectSource.SerialDsr:
					return new SerialCarrierDetector(settings.SerialPort, useDsr: true, settings.CarrierDetectInverted, logger);
				case CarrierDetectSource.Cm108Gpio:
					logger.Warning("CM108 GPIO carrier detect is not yet implemented; using audio squelch instead.");
					return new NullCarrierDetector();
				case CarrierDetectSource.None:
				default:
					return new NullCarrierDetector();
			}
		}

		private static (Mdc1200Decoder, EmergencyToneDetector, IEmergencyAlertSink) BuildSignaling(EmergencyOptions o, ILogger logger)
		{
			if (o == null || (!o.DetectMdc1200 && !o.DetectTones))
				return (null, null, null);

			var mdc = o.DetectMdc1200 ? new Mdc1200Decoder(new Mdc1200Settings(), AudioFormat.SampleRate) : null;
			var emergency = o.DetectTones
				? new EmergencyToneDetector(new EmergencyToneSettings { Frequencies = o.ToneFrequencies ?? new List<double>() }, AudioFormat.SampleRate)
				: null;
			var sink = new ResgridEmergencyAlertSink(logger, o.CreateCall, o.CallPriority, string.IsNullOrWhiteSpace(o.DispatchList) ? null : o.DispatchList);
			return (mdc, emergency, sink);
		}
	}
}
#endif
