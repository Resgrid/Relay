#if NET10_0_WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Ports;
using Resgrid.Audio.Core.Radio;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Audio.Voice;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Connection;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Audio.Voice.LiveKit;
using Resgrid.Audio.Voice.Recording;
using Resgrid.Providers.ApiClient.V4;
using Serilog;

namespace Resgrid.Relay.Engine.Voice
{
	/// <summary>
	/// 'radio' mode (Windows): bidirectional bridge between a physically-attached radio
	/// and a Resgrid PTT channel, with anti-static squelch, PTT keying, optional
	/// recording, and emergency/MDC-1200 detection.
	/// </summary>
	public static class RadioMode
	{
		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken)
		{
			using var apiClient = new ResgridV4ApiClient(options.Resgrid);
			var voiceApi = new VoiceApi(apiClient);
			var callsApi = new CallsApi(apiClient);

			var deptId = string.IsNullOrWhiteSpace(options.Voice.DepartmentId) ? null : options.Voice.DepartmentId;
			var transport = new LiveKitVoiceTransport(logger, options.Voice.PublishQueueMs);
			var provider = new ResgridVoiceChannelProvider(logger, voiceApi);

			if (options.Voice.EnforceSeatLimit)
			{
				var canConnect = await provider.CanConnectAsync(deptId, cancellationToken).ConfigureAwait(false);
				if (canConnect == false)
				{
					logger.Error("Voice seat limit reached for this department; not connecting.");
					return 1;
				}
			}

			await using var manager = new VoiceRoomManager(transport, logger);
			var channel = await provider.GetChannelAsync(options.Voice.Channel, deptId, cancellationToken).ConfigureAwait(false);
			var session = await manager.JoinAsync(channel, cancellationToken).ConfigureAwait(false);

			var radioSettings = MapSettings(options.Radio, logger);
			// Serial PTT and serial carrier-detect on the same COM port must share one
			// SerialPort instance — opening the same port twice fails on Windows. RunAsync
			// owns it (disposed last, after the borrowing controllers).
			using var sharedSerialPort = TryCreateSharedSerialPort(radioSettings);
			using var device = new NAudioRadioDevice(radioSettings.InputDevice, radioSettings.OutputDevice, logger);
			using var ptt = CreatePtt(radioSettings, logger, sharedSerialPort);
			using var carrier = CreateCarrier(radioSettings, logger, sharedSerialPort);

			var (mdc, emergency, alertSink) = BuildSignaling(options.Radio.Emergency, logger, callsApi);

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

			logger.Information($"Radio bridge running on '{channel.Name}'. Press Ctrl+C to stop.");
			await VoiceModeRuntime.WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);

			if (recorder != null)
				await recorder.DisposeAsync().ConfigureAwait(false);
			if (recorderLog != null)
				await recorderLog.DisposeAsync().ConfigureAwait(false);
			if (recorderDisposables != null)
				foreach (var d in recorderDisposables) d.Dispose();

			await bridge.DisposeAsync().ConfigureAwait(false);
			return 0;
		}

		/// <summary>Live receive-level meter + squelch state to help tune the anti-static threshold.</summary>
		public static async Task<int> RunTuneAsync(RelayHostOptions options, CancellationToken cancellationToken)
		{
			var logger = Serilog.Core.Logger.None;
			var radioSettings = MapSettings(options.Radio, logger);
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
				logger.Information($"[{meter}] {db,6:0.0} dBFS  {(open ? "OPEN  " : "closed")}");
			};

			device.StartReceive();
			logger.Information($"Tuning input device {radioSettings.InputDevice}. Open={radioSettings.Squelch.OpenDbfs} dBFS, Close={radioSettings.Squelch.CloseDbfs} dBFS.");
			logger.Information("Key up the radio (or feed static) and adjust OpenDbfs just above the static floor. Ctrl+C to stop.");

			await VoiceModeRuntime.WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
			device.Dispose();
			return 0;
		}

		private static RadioSettings MapSettings(RadioModeOptions o, ILogger logger)
		{
			var ptt = ParseEnumOrWarn(o.PttMethod, nameof(RadioModeOptions.PttMethod), PttKeyingMethod.Vox, logger);
			var carrier = ParseEnumOrWarn(o.CarrierDetect, nameof(RadioModeOptions.CarrierDetect), CarrierDetectSource.None, logger);

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

		private static TEnum ParseEnumOrWarn<TEnum>(string value, string settingName, TEnum fallback, ILogger logger)
			where TEnum : struct, Enum
		{
			if (string.IsNullOrWhiteSpace(value))
				return fallback;

			if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(TEnum), parsed))
				return parsed;

			logger?.Warning(
				"Invalid Radio {Setting} value '{Value}'; expected one of [{Allowed}]. Falling back to {Fallback}.",
				settingName, value, string.Join(", ", Enum.GetNames(typeof(TEnum))), fallback);
			return fallback;
		}

		private static IPttController CreatePtt(RadioSettings settings, ILogger logger, SerialPort sharedSerialPort = null)
		{
			switch (settings.Ptt)
			{
				case PttKeyingMethod.SerialRts:
					return new SerialPttController(settings.SerialPort, useDtr: false, logger, sharedSerialPort);
				case PttKeyingMethod.SerialDtr:
					return new SerialPttController(settings.SerialPort, useDtr: true, logger, sharedSerialPort);
				case PttKeyingMethod.Cm108:
					return new Cm108PttController(settings.Cm108VendorId, settings.Cm108ProductId, settings.Cm108GpioPin, logger);
				case PttKeyingMethod.Vox:
				default:
					return new VoxPttController();
			}
		}

		private static ICarrierDetector CreateCarrier(RadioSettings settings, ILogger logger, SerialPort sharedSerialPort = null)
		{
			switch (settings.CarrierDetect)
			{
				case CarrierDetectSource.SerialCts:
					return new SerialCarrierDetector(settings.SerialPort, useDsr: false, settings.CarrierDetectInverted, logger, sharedSerialPort);
				case CarrierDetectSource.SerialDsr:
					return new SerialCarrierDetector(settings.SerialPort, useDsr: true, settings.CarrierDetectInverted, logger, sharedSerialPort);
				case CarrierDetectSource.Cm108Gpio:
					logger.Warning("CM108 GPIO carrier detect is not yet implemented; using audio squelch instead.");
					return new NullCarrierDetector();
				case CarrierDetectSource.None:
				default:
					return new NullCarrierDetector();
			}
		}

		/// <summary>
		/// Returns a single shared <see cref="SerialPort"/> when both PTT and carrier-detect
		/// are serial (they both use <see cref="RadioSettings.SerialPort"/>), so the COM port
		/// is opened only once; otherwise null so each component opens/owns its own port.
		/// The caller owns and disposes the returned port.
		/// </summary>
		private static SerialPort TryCreateSharedSerialPort(RadioSettings settings)
		{
			bool serialPtt = settings.Ptt == PttKeyingMethod.SerialRts || settings.Ptt == PttKeyingMethod.SerialDtr;
			bool serialCarrier = settings.CarrierDetect == CarrierDetectSource.SerialCts || settings.CarrierDetect == CarrierDetectSource.SerialDsr;

			if (serialPtt && serialCarrier && !string.IsNullOrWhiteSpace(settings.SerialPort))
				return new SerialPort(settings.SerialPort) { ReadTimeout = 200, WriteTimeout = 200 };

			return null;
		}

		private static (Mdc1200Decoder, EmergencyToneDetector, IEmergencyAlertSink) BuildSignaling(EmergencyOptions o, ILogger logger, IResgridCallsApi callsApi)
		{
			if (o == null || (!o.DetectMdc1200 && !o.DetectTones))
				return (null, null, null);

			var mdc = o.DetectMdc1200 ? new Mdc1200Decoder(new Mdc1200Settings(), AudioFormat.SampleRate) : null;
			var emergency = o.DetectTones
				? new EmergencyToneDetector(new EmergencyToneSettings { Frequencies = o.ToneFrequencies ?? new List<double>() }, AudioFormat.SampleRate)
				: null;
			var sink = new ResgridEmergencyAlertSink(logger, o.CreateCall, o.CallPriority, string.IsNullOrWhiteSpace(o.DispatchList) ? null : o.DispatchList, callsApi);
			return (mdc, emergency, sink);
		}
	}
}
#endif
