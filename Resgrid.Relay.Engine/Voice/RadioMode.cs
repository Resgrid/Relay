#if NET10_0_WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
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
			apiClient.Warning += message => logger?.Warning("Resgrid API client: {Warning}", message);
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

			// Session joined and still connected — LiveKit is up. A session the SDK is already reconnecting keeps the Degraded
			// pill the monitor set (it recovers to Connected on the SDK's reconnect) instead of being reported Connected here.
			if (status != null && monitor.IsUsable)
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
		/// side. Throws when the audio device, serial port or CM108 interface can't be opened, <see cref="OperationCanceledException"/>
		/// when cancelled (PTT released), and <see cref="PttReleaseException"/> when PTT could not be released.
		/// </summary>
		public static async Task RunTransmitTestAsync(RelayHostOptions options, ILogger logger, CancellationToken cancellationToken, int toneMs = 2000)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			// A cancel that arrived while this was queued must not open the hardware, let alone key it.
			cancellationToken.ThrowIfCancellationRequested();

			var settings = MapSettings(options.Radio, logger);
			using var sharedSerialPort = TryCreateSharedSerialPort(settings);
			using var device = new NAudioRadioDevice(settings.InputDevice, settings.OutputDevice, logger);
			using var ptt = CreatePtt(settings, logger, sharedSerialPort);

			await RunTransmitTestAsync(device, ptt, settings, logger, cancellationToken, toneMs).ConfigureAwait(false);
		}

		// The transmit test hands the tone to the device in small pieces, never more than this far ahead of playback: the
		// NAudio device buffers 5 s and silently drops what overflows, so a 10 s tone (plus lead) queued at once lost its end.
		private const int TransmitTestChunkMs = 100;
		private static readonly TimeSpan TransmitTestMaxQueued = TimeSpan.FromMilliseconds(1000);

		/// <summary>
		/// The transmit test on an already-open <paramref name="device"/> and <paramref name="ptt"/> line (which the caller owns
		/// and disposes). Never keys after <paramref name="cancellationToken"/> is cancelled; once a key is attempted (also one
		/// that failed), PTT is released on every path — completion, cancellation, a device fault or any other failure — before
		/// this returns or throws.
		/// <list type="bullet">
		/// <item>PTT could not be released (every retry failed): <see cref="PttReleaseException"/>, whatever else went wrong.</item>
		/// <item>The device faulted (<see cref="IRadioDevice.Faulted"/>) or stopped playing: <see cref="InvalidOperationException"/>.</item>
		/// <item>Cancelled: <see cref="OperationCanceledException"/>, with PTT released.</item>
		/// </list>
		/// </summary>
		public static async Task RunTransmitTestAsync(IRadioDevice device, IPttController ptt, RadioSettings settings, ILogger logger,
			CancellationToken cancellationToken, int toneMs = 2000)
		{
			if (device == null)
				throw new ArgumentNullException(nameof(device));
			if (ptt == null)
				throw new ArgumentNullException(nameof(ptt));
			if (settings == null)
				throw new ArgumentNullException(nameof(settings));

			cancellationToken.ThrowIfCancellationRequested();

			var leadMs = settings.Ptt == PttKeyingMethod.Vox ? 0 : Math.Clamp(settings.TxLeadMs, 0, 3000);
			toneMs = Math.Clamp(toneMs, 200, 10000);
			var audio = BuildTransmitTestAudio(settings, leadMs, toneMs);

			// Watch the device from before playback starts: an output that stops mid-tone (USB glitch/unplug) otherwise let the
			// test sit out its delays and report success for a tone nobody heard.
			RadioFaultEventArgs fault = null;
			using var testCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			EventHandler<RadioFaultEventArgs> onFault = (_, e) =>
			{
				Interlocked.CompareExchange(ref fault, e ?? new RadioFaultEventArgs(null), null);
				try { testCts.Cancel(); }
				catch (ObjectDisposedException) { }
			};

			var keyAttempted = false;
			var keyed = false;
			var released = true;
			ExceptionDispatchInfo failure = null;
			device.Faulted += onFault;
			try
			{
				try
				{
					device.StartTransmit();

					// Last point before anything goes on the air: a cancel (or fault) that arrived while the devices opened must
					// not key the radio.
					testCts.Token.ThrowIfCancellationRequested();
					keyAttempted = true;
					if (!ptt.Key())
						throw new InvalidOperationException($"Transmit test: could not key the radio's PTT line ({settings.Ptt}). Check the interface cable and port.");
					keyed = true;
					logger?.Information("Transmit test: PTT keyed ({Ptt}), {Lead} ms lead, {Tone} ms 1 kHz tone", settings.Ptt, leadMs, toneMs);

					await PlayTransmitTestAudioAsync(device, audio, testCts.Token).ConfigureAwait(false);

					// Hold PTT until the output device's own buffers have played out, then the tail. Unkeying as soon as the queue
					// emptied cut the end of the tone off by the output latency.
					await Task.Delay(device.OutputLatency + TimeSpan.FromMilliseconds(Math.Max(settings.TxTailMs, 100)), testCts.Token).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					failure = ExceptionDispatchInfo.Capture(ex);
				}

				// Once a key was attempted, also one that failed: after a failed Key() the line state is unknown (the write can
				// half-happen), so it gets the same bounded release, and a release that never succeeds is reported as one. A key
				// failure the release clears is still reported as the key failure.
				if (keyAttempted)
					released = await ReleasePttAsync(ptt).ConfigureAwait(false);
			}
			finally
			{
				// Device cleanup is unconditional; only then is the outcome reported.
				try { device.StopTransmit(); }
				catch (Exception ex) { logger?.Warning(ex, "Transmit test: closing the transmit audio path failed"); }
				device.Faulted -= onFault;
			}

			if (!released)
			{
				logger?.Error("Transmit test: the PTT line ({Ptt}) could not be released — the radio may still be transmitting. Disconnect the interface.", settings.Ptt);
				throw new PttReleaseException(settings.Ptt.ToString(), failure?.SourceException);
			}

			if (keyed)
				logger?.Information("Transmit test: PTT released");

			var deviceFault = Volatile.Read(ref fault);
			if (deviceFault != null)
				throw new InvalidOperationException($"Transmit test: {deviceFault.Reason}; the test tone was interrupted.", deviceFault.Exception);

			failure?.Throw();
		}

		/// <summary>The key-up lead (silence) followed by the 1 kHz tone at the configured transmit gain.</summary>
		private static short[] BuildTransmitTestAudio(RadioSettings settings, int leadMs, int toneMs)
		{
			var tones = new ToneGenerator(AudioFormat.SampleRate);
			var tone = tones.Sine(1000, toneMs, 0.3);
			if (Math.Abs(settings.TxGain - 1.0) > 1e-6)
				Resampler.ApplyGain(tone, settings.TxGain);

			var lead = leadMs > 0 ? tones.Silence(leadMs) : Array.Empty<short>();
			var audio = new short[lead.Length + tone.Length];
			Array.Copy(tone, 0, audio, lead.Length, tone.Length);
			return audio;
		}

		/// <summary>
		/// Feeds <paramref name="audio"/> to the device in <see cref="TransmitTestChunkMs"/> pieces, keeping at most
		/// <see cref="TransmitTestMaxQueued"/> ahead of playback (by the device's own backlog and by the clock, for a device that
		/// cannot report one), then waits for the queue to drain. Throws when the output stops consuming audio.
		/// </summary>
		private static async Task PlayTransmitTestAudioAsync(IRadioDevice device, short[] audio, CancellationToken cancellationToken)
		{
			var samplesPerMs = AudioFormat.SampleRate / 1000;
			var chunkSamples = TransmitTestChunkMs * samplesPerMs;
			var total = TimeSpan.FromMilliseconds((double)audio.Length / samplesPerMs);
			var started = DateTime.UtcNow;
			var deadline = started + total + TimeSpan.FromSeconds(2);

			var offset = 0;
			while (offset < audio.Length)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var sent = TimeSpan.FromMilliseconds((double)offset / samplesPerMs);
				var elapsed = DateTime.UtcNow - started;
				if (device.BufferedTransmit < TransmitTestMaxQueued && sent < elapsed + TransmitTestMaxQueued)
				{
					var count = Math.Min(chunkSamples, audio.Length - offset);
					var chunk = new short[count];
					Array.Copy(audio, offset, chunk, 0, count);
					device.Transmit(chunk);
					offset += count;
					continue;
				}

				if (DateTime.UtcNow >= deadline)
					throw new InvalidOperationException("Transmit test: the output device stopped playing audio; the test tone was cut short.");
				await Task.Delay(20, cancellationToken).ConfigureAwait(false);
			}

			// Everything is queued: hold until it has left the queue (and, for a device that can't report a backlog, until it
			// has had time to play).
			while (device.BufferedTransmit > TimeSpan.Zero || DateTime.UtcNow - started < total)
			{
				if (DateTime.UtcNow >= deadline)
					throw new InvalidOperationException("Transmit test: the output device stopped playing audio; the test tone was cut short.");
				await Task.Delay(20, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>Unkeys, retrying for about a second: a failed unkey leaves the transmitter on the air. False when every attempt failed.</summary>
		private static async Task<bool> ReleasePttAsync(IPttController ptt)
		{
			if (TryUnkey(ptt))
				return true;

			for (int attempt = 0; attempt < 10; attempt++)
			{
				await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
				if (TryUnkey(ptt))
					return true;
			}
			return false;
		}

		private static bool TryUnkey(IPttController ptt)
		{
			try { return ptt.Unkey(); }
			catch { return false; }
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

	/// <summary>
	/// The radio's PTT line could not be released (every unkey attempt failed), so the transmitter may still be on the air.
	/// Thrown by <see cref="RadioMode.RunTransmitTestAsync(RelayHostOptions, ILogger, CancellationToken, int)"/> in place of any
	/// other outcome (completion, cancellation or another failure, which is kept as <see cref="Exception.InnerException"/>): the
	/// operator has to disconnect the radio interface or turn the radio off.
	/// </summary>
	public sealed class PttReleaseException : InvalidOperationException
	{
		public PttReleaseException(string pttMethod, Exception innerException = null)
			: base($"The radio's PTT line ({pttMethod}) could not be released, so the radio may still be transmitting. " +
				"Unplug the radio interface (its USB or serial cable) or turn the radio off now, then check the wiring.", innerException)
		{
			PttMethod = pttMethod;
		}

		/// <summary>The PTT keying method that failed to release (Cm108, SerialRts, SerialDtr, Vox).</summary>
		public string PttMethod { get; }
	}
}
#endif
