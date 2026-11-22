#if NET10_0_WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Ports;
using Resgrid.Audio.Core.Radio;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Audio.Voice;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Audio;
using Resgrid.Audio.Voice.Connection;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Audio.Voice.LiveKit;
using Resgrid.Audio.Voice.Recording;
using Resgrid.Audio.Voice.ToneOut;
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
		public static async Task<int> RunAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, RelayStatus status = null)
		{
			VoiceModeRuntime.EnsureUserTokenAuth(options, "radio");
			using var apiClient = new ResgridV4ApiClient(options.Resgrid);
			var voiceApi = new VoiceApi(apiClient);
			var callsApi = new CallsApi(apiClient);

			var deptId = string.IsNullOrWhiteSpace(options.Voice.DepartmentId) ? null : options.Voice.DepartmentId;
			var transport = new LiveKitVoiceTransport(logger, options.Voice.PublishQueueMs);
			var provider = new ResgridVoiceChannelProvider(logger, voiceApi);

			if (!await VoiceModeRuntime.HasVoiceSeatAsync(options, provider, deptId, cancellationToken).ConfigureAwait(false))
			{
				logger.Error("Voice seat limit reached for this department; not connecting.");
				return 1;
			}

			await using var manager = new VoiceRoomManager(transport, logger);
			var channel = await provider.GetChannelAsync(options.Voice.Channel, deptId, cancellationToken).ConfigureAwait(false);
			var session = await manager.JoinAsync(channel, cancellationToken).ConfigureAwait(false);

			// Follow the connection from the moment the join returns: a drop during the device/serial/HID setup below used to
			// go unnoticed (the handler was attached after it), leaving the bridge running on a dead session. A hard
			// disconnect cancels the linked token so the bridge unwinds and we rethrow (below) for the resilience layer to
			// rejoin; a transient SDK "reconnecting" only degrades the status pill and is left to auto-recover.
			using var disconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			using var monitor = new VoiceSessionMonitor(session, status);
			_ = monitor.Disconnected.ContinueWith(_ =>
			{
				try { disconnectCts.Cancel(); }
				catch (ObjectDisposedException) { }
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			await monitor.EnsureConnectedAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

			// Session joined and still connected — LiveKit is up.
			if (status != null)
				status.LiveKit = ConnectionState.Connected;

			var radioSettings = MapSettings(options.Radio, logger);
			WarnAboutProfile(options.Radio, radioSettings, logger);
			// Serial PTT and serial carrier-detect on the same COM port must share one
			// SerialPort instance — opening the same port twice fails on Windows. RunAsync
			// owns it (disposed last, after the borrowing controllers).
			using var sharedSerialPort = TryCreateSharedSerialPort(radioSettings);
			using var device = new NAudioRadioDevice(radioSettings.InputDevice, radioSettings.OutputDevice, logger);
			using var ptt = CreatePtt(radioSettings, logger, sharedSerialPort);
			using var carrier = CreateCarrier(radioSettings, logger, sharedSerialPort);

			var (mdc, emergency, alertSink) = BuildSignaling(options.Radio.Emergency, logger, callsApi, deptId);

			await using var bridge = new RadioBridge(device, ptt, carrier, radioSettings, logger, mdc, emergency, alertSink);

			// Mirror the bridge's live TX/RX state and receive level onto the status surface — wire this BEFORE
			// StartAsync so no initial transmit/receive flip is missed.
			if (status != null)
			{
				bridge.TxRxChanged += (_, __) =>
				{
					status.Transmitting = bridge.Transmitting;
					status.Receiving = bridge.Receiving;
				};
				bridge.RxLevelSampled += (_, level) =>
				{
					status.InputDbfs = level.Dbfs;
					status.SquelchOpen = level.SquelchOpen;
				};
			}

			// The audio device stopping (USB glitch/replug), channel publishing that keeps failing, or a PTT line that stopped
			// responding leave the bridge unable to pass audio while the session itself is fine. Unwind and rethrow so the
			// resilience layer reopens the devices, the serial port and the HID interface instead of reporting Running while
			// deaf and keying dead carrier.
			bridge.Faulted += (_, __) =>
			{
				try { disconnectCts.Cancel(); }
				catch (ObjectDisposedException) { }
			};

			TransmissionRecorder recorder = null;
			EventHandler<short[]> radioRecordHandler = null;
			List<IDisposable> recorderDisposables = null;
			ITransmissionLog recorderLog = null;
			try
			{
				await bridge.StartAsync(session, disconnectCts.Token).ConfigureAwait(false);

				if (options.Radio.RecordWhileBridging)
				{
					var (stores, disposables) = RecordMode.BuildStores(options.Recorder, logger);
					recorderDisposables = disposables;
					recorderLog = RecordMode.BuildLog(options.Recorder, logger);
					recorder = new TransmissionRecorder(session, options.Recorder.Segmentation, stores, recorderLog, logger,
						RecordMode.ResolveFallbackDirectory(options.Recorder));
					recorder.Start();

					var radioRecorder = recorder;
					var radioParticipant = new VoiceParticipant("radio", "radio", $"Radio ({channel.Name})", "radio");
					radioRecordHandler = (_, pcm) =>
						radioRecorder.Record(new VoiceAudioFrame(radioParticipant, "radio-rx", pcm, DateTime.UtcNow));
					bridge.RadioAudioForwarded += radioRecordHandler;
				}

				logger.Information($"Radio bridge running on '{channel.Name}'. Press Ctrl+C to stop.");
				await VoiceModeRuntime.WaitForCancellationAsync(disconnectCts.Token).ConfigureAwait(false);

				// Woken by a fault (not the user shutdown) ⇒ throw so resilience rebuilds the bridge.
				if (!cancellationToken.IsCancellationRequested)
				{
					var fault = bridge.Fault;
					if (fault != null)
						throw new InvalidOperationException($"Radio bridge failed ({fault.Reason}); reopening the radio interface", fault.Exception);
					if (monitor.IsDisconnected)
						throw new InvalidOperationException($"LiveKit radio session disconnected ({monitor.Disconnected.Result}); reconnecting");
				}
			}
			finally
			{
				if (radioRecordHandler != null)
					bridge.RadioAudioForwarded -= radioRecordHandler;
				if (recorder != null)
					await recorder.DisposeAsync().ConfigureAwait(false);
				if (recorderLog != null)
					await recorderLog.DisposeAsync().ConfigureAwait(false);
				if (recorderDisposables != null)
					foreach (var d in recorderDisposables) d.Dispose();
			}

			// bridge is disposed by its `await using` scope on method exit — no explicit call.
			return 0;
		}

		/// <summary>
		/// Live receive-level metering + squelch state to help tune the anti-static threshold.
		/// Each ~150 ms sample is reported via <paramref name="progress"/> (the engine renders
		/// nothing itself); <paramref name="logger"/> is used only for device diagnostics.
		/// </summary>
		public static async Task<int> RunTuneAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, IProgress<TuneSample> progress = null)
		{
			var radioSettings = MapSettings(options.Radio, logger);
			var gate = new SquelchGate(radioSettings.Squelch, AudioFormat.SampleRate);
			using var device = new NAudioRadioDevice(radioSettings.InputDevice, radioSettings.OutputDevice, logger);

			var lastSampleTicks = 0L;
			device.SamplesReceived += (_, frame) =>
			{
				bool open = gate.Process(frame);
				var now = DateTime.UtcNow.Ticks;
				if (now - lastSampleTicks < TimeSpan.TicksPerMillisecond * 150)
					return;
				lastSampleTicks = now;

				// Surface the live level/squelch so the console meter (and the WPF Radio
				// tuner) can render it; the engine itself stays UI-free.
				progress?.Report(new TuneSample(gate.LastDbfs, open));
			};

			device.StartReceive();
			await VoiceModeRuntime.WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
			return 0;
		}

		/// <summary>
		/// Keys the radio and plays a 1 kHz test tone (after the configured key-up lead) without joining a channel, so the PTT
		/// wiring, transmit audio level and <see cref="RadioModeOptions.TxLeadMs"/> can be checked against a listener on the far
		/// side. Throws when the audio device, serial port or CM108 interface can't be opened.
		/// </summary>
		public static async Task RunTransmitTestAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, int toneMs = 2000)
		{
			var settings = MapSettings(options.Radio, logger);
			var leadMs = settings.Ptt == PttKeyingMethod.Vox ? 0 : Math.Clamp(settings.TxLeadMs, 0, 3000);
			toneMs = Math.Clamp(toneMs, 200, 10000);

			var tones = new ToneGenerator(AudioFormat.SampleRate);
			var tone = tones.Sine(1000, toneMs, 0.3);
			if (Math.Abs(settings.TxGain - 1.0) > 1e-6)
				Resampler.ApplyGain(tone, settings.TxGain);

			using var sharedSerialPort = TryCreateSharedSerialPort(settings);
			using var device = new NAudioRadioDevice(settings.InputDevice, settings.OutputDevice, logger);
			using var ptt = CreatePtt(settings, logger, sharedSerialPort);

			device.StartTransmit();
			try
			{
				if (!ptt.Key())
					throw new InvalidOperationException($"Transmit test: could not key the radio's PTT line ({settings.Ptt}). Check the interface cable and port.");
				logger?.Information("Transmit test: PTT keyed ({Ptt}), {Lead} ms lead, {Tone} ms 1 kHz tone", settings.Ptt, leadMs, toneMs);
				if (leadMs > 0)
					device.Transmit(tones.Silence(leadMs));
				device.Transmit(tone);

				// Hold PTT until the tone has actually played: the queue has drained (bounded, in case the device never reports
				// empty) and the output device's own buffers have played out, then the tail. Unkeying on a fixed lead+tone delay
				// cut the end of the tone off by the output latency.
				var queued = TimeSpan.FromMilliseconds(leadMs + toneMs);
				var started = DateTime.UtcNow;
				await Task.Delay(queued, cancellationToken).ConfigureAwait(false);
				var drainDeadline = started + queued + TimeSpan.FromSeconds(2);
				while (device.BufferedTransmit > TimeSpan.Zero && DateTime.UtcNow < drainDeadline)
					await Task.Delay(20, cancellationToken).ConfigureAwait(false);

				await Task.Delay(device.OutputLatency + TimeSpan.FromMilliseconds(Math.Max(settings.TxTailMs, 100)), cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				if (!ptt.Unkey())
				{
					// A failed unkey leaves the transmitter on the air: retry before giving up and saying so loudly.
					var released = false;
					for (int attempt = 0; attempt < 10 && !released; attempt++)
					{
						Thread.Sleep(100);
						released = ptt.Unkey();
					}
					if (!released)
						logger?.Error("Transmit test: the PTT line could not be released — the radio may still be transmitting. Disconnect the interface.");
				}
				device.StopTransmit();
				logger?.Information("Transmit test: PTT released");
			}
		}

		private static void WarnAboutProfile(RadioModeOptions o, RadioSettings settings, ILogger logger)
		{
			var profile = RadioProfiles.Find(o.Profile);
			logger?.Information("Radio profile: {Profile}", profile.DisplayName);
			if (!profile.IsPushToTalkOverCellular)
				return;

			if (settings.Ptt == PttKeyingMethod.Vox)
				logger?.Warning("Radio profile {Profile} is a cellular push-to-talk radio but PttMethod is Vox: it must request the floor " +
					"before it passes audio, so key it with a CM108 GPIO or serial RTS/DTR line or the start of each transmission is lost.", profile.DisplayName);
			else if (settings.TxLeadMs <= 0)
				logger?.Warning("Radio profile {Profile} is a cellular push-to-talk radio but TxLeadMs is 0: the first words of each " +
					"transmission will be clipped while it waits for the floor. Start with {Lead} ms.", profile.DisplayName, profile.TxLeadMs);
		}

		private static RadioSettings MapSettings(RadioModeOptions o, ILogger logger)
		{
			var ptt = ParseEnumOrWarn(o.PttMethod, nameof(RadioModeOptions.PttMethod), PttKeyingMethod.Vox, logger);
			var carrier = ParseEnumOrWarn(o.CarrierDetect, nameof(RadioModeOptions.CarrierDetect), CarrierDetectSource.None, logger);

			// Carrier squelch needs a carrier source. Without one (None, or the unimplemented CM108 GPIO) the null detector
			// reports a carrier permanently: static was relayed nonstop and, with AntiLoop, nothing was ever transmitted.
			var squelch = o.Squelch ?? new SquelchSettings();
			if (squelch.Mode == SquelchMode.Carrier && (carrier == CarrierDetectSource.None || carrier == CarrierDetectSource.Cm108Gpio))
			{
				logger?.Warning("Radio Squelch.Mode=Carrier needs CarrierDetect=SerialCts or SerialDsr (got {CarrierDetect}); using the audio-level (Vox) squelch instead.", carrier);
				squelch = WithMode(squelch, SquelchMode.Vox);
			}
			else if (squelch.Mode == SquelchMode.Ctcss && !(squelch.CtcssFrequency > 0))
			{
				// With no tone to look for the CTCSS squelch never opens: nothing on the air would ever reach the channel.
				logger?.Warning("Radio Squelch.Mode=Ctcss needs Squelch.CtcssFrequency (the PL tone in Hz, e.g. 100.0; got {Frequency}); using the audio-level (Vox) squelch instead.",
					squelch.CtcssFrequency);
				squelch = WithMode(squelch, SquelchMode.Vox);
			}

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
				TxLeadMs = o.TxLeadMs,
				TxHangMs = o.TxHangMs,
				TxTailMs = o.TxTailMs,
				MaxTransmitSeconds = Math.Max(0, o.MaxTransmitSeconds),
				AntiLoop = o.AntiLoop,
				TxGain = o.TxGain,
				RxGain = o.RxGain,
				HighPassCutoffHz = o.HighPassCutoffHz,
				Squelch = squelch
			};
		}

		private static SquelchSettings WithMode(SquelchSettings s, SquelchMode mode) => new SquelchSettings
		{
			Mode = mode,
			OpenDbfs = s.OpenDbfs,
			CloseDbfs = s.CloseDbfs,
			HangMs = s.HangMs,
			CtcssFrequency = s.CtcssFrequency,
			CtcssMinStrength = s.CtcssMinStrength
		};

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

		private static (Mdc1200Decoder, EmergencyToneDetector, IEmergencyAlertSink) BuildSignaling(EmergencyOptions o, ILogger logger, IResgridCallsApi callsApi, string departmentId)
		{
			if (o == null || (!o.DetectMdc1200 && !o.DetectTones))
				return (null, null, null);

			if (o.DetectMdc1200)
				logger.Warning("MDC-1200 detection is experimental: the decoder does not implement the standard MDC-1200 framing " +
					"(40-bit sync, convolutional FEC, interleaving), so emergency bursts from Motorola radios are not decoded. " +
					"Do not rely on it for emergency alerting; use DetectTones or the radio system's own alerting.");

			var mdc = o.DetectMdc1200 ? new Mdc1200Decoder(new Mdc1200Settings(), AudioFormat.SampleRate) : null;
			var emergency = o.DetectTones
				? new EmergencyToneDetector(new EmergencyToneSettings { Frequencies = o.ToneFrequencies ?? new List<double>() }, AudioFormat.SampleRate)
				: null;
			var sink = new ResgridEmergencyAlertSink(logger, o.CreateCall, o.CallPriority, string.IsNullOrWhiteSpace(o.DispatchList) ? null : o.DispatchList, callsApi,
				departmentId, TimeSpan.FromSeconds(Math.Max(0, o.CooldownSeconds)));
			return (mdc, emergency, sink);
		}
	}
}
#endif
