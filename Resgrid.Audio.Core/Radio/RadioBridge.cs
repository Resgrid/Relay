using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Audio;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Audio.Voice.ToneOut;
using Serilog;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>A receive-level sample from <see cref="RadioBridge.RxLevelSampled"/>.</summary>
	public sealed class RadioRxLevel : EventArgs
	{
		public RadioRxLevel(double dbfs, bool squelchOpen)
		{
			Dbfs = dbfs;
			SquelchOpen = squelchOpen;
		}

		public double Dbfs { get; }
		public bool SquelchOpen { get; }
	}

	/// <summary>
	/// The bidirectional bridge between a physical radio and a Resgrid PTT channel.
	///
	/// RX path: radio receive audio → squelch/anti-static gate → high-pass + gain →
	/// publish to the LiveKit channel.
	/// TX path: channel audio → key PTT → play to the radio mic, with hang time, an
	/// optional courtesy tone, and half-duplex anti-loop arbitration so audio is never
	/// relayed back to the side it came from.
	/// </summary>
	public sealed class RadioBridge : IAsyncDisposable
	{
		private readonly IRadioDevice _device;
		private readonly IPttController _ptt;
		private readonly ICarrierDetector _carrier;
		private readonly RadioSettings _settings;
		private readonly ILogger _logger;

		private readonly SquelchGate _squelch;
		private readonly CtcssDetector _ctcss;
		private readonly HighPassFilter _highPass;
		private readonly SoftLimiter _limiter = new SoftLimiter(-1.0);
		private readonly ToneGenerator _tones = new ToneGenerator(AudioFormat.SampleRate);

		private readonly Mdc1200Decoder _mdc;
		private readonly EmergencyToneDetector _emergency;
		private readonly IEmergencyAlertSink _alertSink;

		private readonly Channel<short[]> _publishQueue = Channel.CreateBounded<short[]>(
			new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

		private IVoiceRoomSession _session;
		private IAudioPublisher _publisher;
		private Task _publishPump;
		private Timer _txTimer;
		private CancellationTokenSource _cts;

		private volatile bool _transmitting;   // channel → radio active (suppress RX→channel)
		private volatile bool _receiving;       // radio → channel active (suppress TX→radio)

		// ---- Transmit state: everything below is read and written under _txLock (channel-audio threads and the tail timer).
		// Doing key-up and the tail decision under one lock means a timer tick can never land between keying and recording the
		// new transmission's start (it used to unkey a fresh transmission instantly, or beep at its start).
		private readonly object _txLock = new object();
		private bool _wantKeyed;              // the bridge's transmit decision; the PTT line may lag it after a failed write
		private long _keyedAtTicks;
		private long _lastTxTicks;             // last audible channel audio
		private long _txQueuedUntilTicks;      // when the audio queued to the device finishes playing (bridge's own estimate)
		private long _deviceDrainedAtTicks;    // when the device's reported queue empties (or emptied)
		private bool _courtesyPlayed;
		private long _pttMismatchSinceTicks;   // PTT line not in the wanted state since (0 = in sync)
		private long _lastPttRetryTicks;
		private long _lastDeviceBacklogTicks;  // the device's reported backlog at the previous tail tick
		private long _lastDevicePlayedTicks;   // the device's reported playback progress at the previous tail tick
		private long _backlogStuckSinceTicks;  // since when that backlog has not been played from (0 = empty)
		private bool _stopping;                // DisposeAsync's final release has run: nothing keys the radio or queues audio after it

		// A key-up whose PTT write failed: playing the lead and speech to a radio that is not keyed loses them (a retry that
		// keys it 250 ms later would start mid-sentence), so the audio is held back until the line keys, then the lead starts
		// from there. Bounded (the most recent audio is kept) and dropped if the transmission ends first.
		private bool _keyPending;
		private readonly Queue<short[]> _heldTransmit = new Queue<short[]>();
		private int _heldTransmitSamples;
		private static readonly int MaxHeldTransmitSamples = AudioFormat.SampleRate; // 1 s

		// An output device that stops pulling audio without raising PlaybackStopped (a silent USB stall) reports the same backlog
		// forever. Waiting for it to drain held the radio keyed until the transmit time-out (or for good with no time-out), so
		// the unkey waits at most this long past the bridge's own estimate of the queue...
		private static readonly long MaxDrainOverrunTicks = TimeSpan.FromSeconds(1).Ticks;
		// ...and a backlog that has not shrunk for this long means the output is dead: the bridge faults so it is reopened.
		private static readonly long TransmitStallFaultTicks = TimeSpan.FromSeconds(2).Ticks;

		// Transmit time-out: set when MaxTransmitSeconds cuts a talker off; the talker's audio is ignored until they go quiet.
		private volatile bool _timeOutLockout;

		// After unkeying on a loopback interface the receive gate still holds whatever our own transmission opened it with (up
		// to the squelch hang), plus the capture buffers still carry the end of it; keep the gate shut briefly and start clean.
		// The reset itself is applied on the capture thread's next frame even when that arrives after the guard (capture
		// paused across it), so the first frame after the pause cannot ride the hang our own transmission armed.
		private long _squelchGuardUntilTicks;
		private int _squelchResetPending;
		private static readonly long SquelchGuardTicks = TimeSpan.FromMilliseconds(150).Ticks;

		// Released, but the PTT line has not confirmed it (a failed unkey write): the radio may still be on the air, so receive
		// audio stays suppressed as while transmitting, and the squelch guard starts only once the line actually releases.
		private volatile bool _unkeyPending;

		// Squelch Off: what is left of the squelch hang since the last audible receive frame (capture thread only).
		private double _offHangRemainingMs;

		private RadioFaultEventArgs _fault;

		// Channel -> radio floor control. LiveKit delivers a 10 ms frame for EVERY subscribed remote track continuously, as
		// digital silence or comfort noise while that participant is not talking. Keying on any frame kept the transmitter
		// keyed for as long as anyone sat in the room (and, with AntiLoop, blocked all radio receive), and writing every
		// track's frames into the one output interleaved N streams into a garbled, ever-growing backlog. So: only audible
		// audio keys the radio, and one talker (track) holds the floor until it has been quiet for the talker hang.
		private const double TxActivityDbfs = -50.0;

		// Key-up lead: silence queued to the radio right after keying, so the first words reach the mic only once the radio
		// passes audio (PoC/LTE radios wait for a floor grant). Capped well inside the device's 5 s transmit buffer.
		private const int MaxTxLeadMs = 3000;
		private readonly int _txLeadMs;

		// The PTT line is re-driven at this interval while it disagrees with the bridge, and the bridge faults (so its owner
		// reopens the serial port / HID device) when it still disagrees after PttFaultAfter.
		private static readonly long PttRetryTicks = TimeSpan.FromMilliseconds(250).Ticks;
		private static readonly long PttFaultAfterTicks = TimeSpan.FromSeconds(5).Ticks;

		// Channel publishing: a failed frame is dropped and the pump carries on, but a publisher that keeps failing means the
		// channel side is dead and the bridge faults so it is rebuilt.
		internal const int MaxConsecutivePublishFailures = 100;
		internal static readonly TimeSpan PublishFailureWindow = TimeSpan.FromSeconds(10);
		private static readonly long PublishWarnIntervalTicks = TimeSpan.FromSeconds(5).Ticks;
		// A write that never completes (a stalled LiveKit audio source) blocked the pump with no fault while the bounded queue
		// silently dropped the radio audio behind it. A frame normally waits at most one frame for back-pressure.
		internal static readonly TimeSpan PublishWriteTimeout = TimeSpan.FromSeconds(2);

		// Shutdown: a final unkey that fails is retried a few times before the bridge gives up and says so.
		private const int FinalUnkeyRetries = 5;
		private static readonly TimeSpan FinalUnkeyRetryDelay = TimeSpan.FromMilliseconds(100);
		private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(5);

		// Receive level reporting for the UI meter (throttled; the device delivers ~20 ms frames).
		private static readonly long RxLevelIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;
		private long _lastRxLevelTicks;
		private long _lastRxErrorTicks;

		// Lock order: _txLock, then _talkerLock (never the other way round).
		private readonly object _talkerLock = new object();
		private string _activeTalker;
		private long _floorTurn;               // bumped whenever the floor is taken or cleared (under _talkerLock)
		private long _activeTalkerLastAudibleTicks;
		// When the most recent talker took the floor. Kept after their hang clears the floor, so the radio draining their last
		// words (a PoC lead keeps it keyed for seconds) still counts against their turn, not against the whole keyed session.
		private long _activeTalkerSinceTicks;

		public RadioBridge(
			IRadioDevice device,
			IPttController ptt,
			ICarrierDetector carrier,
			RadioSettings settings,
			ILogger logger,
			Mdc1200Decoder mdc = null,
			EmergencyToneDetector emergency = null,
			IEmergencyAlertSink alertSink = null)
		{
			_device = device;
			_ptt = ptt;
			_carrier = carrier ?? new NullCarrierDetector();
			_settings = settings;
			_logger = logger;

			_squelch = new SquelchGate(settings.Squelch, AudioFormat.SampleRate);
			_ctcss = new CtcssDetector(settings.Squelch.CtcssFrequency, settings.Squelch.CtcssMinStrength, AudioFormat.SampleRate,
				hangMs: settings.Squelch.HangMs);
			_highPass = new HighPassFilter(Math.Max(1, settings.HighPassCutoffHz), AudioFormat.SampleRate);
			_txLeadMs = settings.Ptt == PttKeyingMethod.Vox ? 0 : Math.Clamp(settings.TxLeadMs, 0, MaxTxLeadMs);

			_mdc = mdc;
			_emergency = emergency;
			_alertSink = alertSink;

			if (_emergency != null)
				_emergency.EmergencyDetected += (_, d) => RaiseEmergency("Tone", d.Detail);
			if (_mdc != null)
				_mdc.PacketDecoded += OnMdcPacket;
		}

		public async Task StartAsync(IVoiceRoomSession session, CancellationToken cancellationToken)
		{
			_session = session ?? throw new ArgumentNullException(nameof(session));
			_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			if (_settings.Squelch.Mode == SquelchMode.Ctcss && _settings.Squelch.CtcssFrequency <= 0)
				_logger?.Warning("Radio squelch is Ctcss but no CTCSS frequency is set: the receive squelch will never open.");

			_device.Faulted += OnDeviceFaulted;
			_publisher = await _session.CreatePublisherAsync("radio", cancellationToken).ConfigureAwait(false);
			_publishPump = Task.Run(() => PublishPumpAsync(_cts.Token));

			_session.AudioFrameReceived += OnChannelAudio;
			_device.SamplesReceived += OnRadioAudio;
			_device.StartReceive();
			_device.StartTransmit();

			_txTimer = new Timer(_ => OnTxTimer(), null, 50, 50);
			_logger?.Information("Radio bridge running: {Channel} <-> radio (squelch={Mode}, ptt={Ptt}, txLead={Lead} ms, maxTransmit={Max} s)",
				_session.ChannelName, _settings.Squelch.Mode, _settings.Ptt, _txLeadMs, Math.Max(0, _settings.MaxTransmitSeconds));
		}

		/// <summary>True while channel audio is being keyed to the radio (PTT active).</summary>
		public bool Transmitting => _transmitting;

		/// <summary>True while radio audio is open and passing through to the channel.</summary>
		public bool Receiving => _receiving;

		/// <summary>Raised whenever <see cref="Transmitting"/> or <see cref="Receiving"/> changes.</summary>
		public event EventHandler TxRxChanged;

		/// <summary>
		/// Radio receive audio (after squelch and conditioning) as it is sent to the channel. LiveKit never echoes the bridge's
		/// own published track back to it, so a recorder on the session hears every remote talker but none of the over-the-air
		/// traffic; this is how the radio side gets recorded.
		/// </summary>
		public event EventHandler<short[]> RadioAudioForwarded;

		/// <summary>Raised about every 100 ms with the radio receive level and squelch state, for live metering.</summary>
		public event EventHandler<RadioRxLevel> RxLevelSampled;

		/// <summary>
		/// Raised once, on a thread-pool thread, when the bridge can no longer pass audio: the audio device stopped, publishing
		/// to the channel keeps failing, or the PTT line stopped responding. The radio is unkeyed first; the owner should tear
		/// the bridge down and rebuild it (devices, serial port, HID).
		/// </summary>
		public event EventHandler<RadioFaultEventArgs> Faulted;

		/// <summary>The fault that stopped the bridge, or null while it is healthy.</summary>
		public RadioFaultEventArgs Fault => Volatile.Read(ref _fault);

		private void SetReceiving(bool value)
		{
			if (_receiving == value)
				return;
			_receiving = value;
			RaiseTxRxChanged();
		}

		// Called under _txLock; the caller raises TxRxChanged once it has left the lock.
		private bool UpdateTransmitting(bool value)
		{
			if (_transmitting == value)
				return false;
			_transmitting = value;
			return true;
		}

		private void RaiseTxRxChanged()
		{
			try
			{
				TxRxChanged?.Invoke(this, EventArgs.Empty);
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Radio TX/RX subscriber failed");
			}
		}

		// ---- Radio receive → channel ---------------------------------------------

		private void OnRadioAudio(object sender, short[] frame)
		{
			if (frame == null || frame.Length == 0)
				return;

			// This runs on the capture thread: an exception escaping it ends the device's recording.
			try
			{
				ProcessRadioAudio(frame);
			}
			catch (Exception ex)
			{
				var now = DateTime.UtcNow.Ticks;
				if (now - Interlocked.Read(ref _lastRxErrorTicks) >= PublishWarnIntervalTicks)
				{
					Interlocked.Exchange(ref _lastRxErrorTicks, now);
					_logger?.Warning(ex, "Radio receive frame processing failed");
				}
			}
		}

		private void ProcessRadioAudio(short[] frame)
		{
			// Always feed the signaling decoders (they apply their own thresholds).
			_mdc?.Process(frame);
			_emergency?.Process(frame);

			// Read before the squelch: the unkey publishes the squelch guard/reset before it lifts this, so a frame that sees
			// it lifted also sees the guard.
			var ownTransmission = _settings.AntiLoop && (_transmitting || _unkeyPending);

			bool open = IsSquelchOpen(frame);
			ReportRxLevel(frame, open);

			// Only count as "receiving" when RX audio is actually forwarded to the channel —
			// not merely when squelch opens, and not while AntiLoop suppresses forwarding during
			// transmit (a squelch opening then is our own TX feedback, not real RX, and counting
			// it would make OnChannelAudio's anti-loop gate suppress the in-progress transmit).
			// That includes a release the PTT line has not confirmed yet: the radio may still be on the air.
			if (!open || ownTransmission || Fault != null)
			{
				SetReceiving(false);
				return;
			}

			SetReceiving(true);

			// Copy + condition the audio before publishing.
			var outgoing = (short[])frame.Clone();
			_highPass.Process(outgoing);
			if (Math.Abs(_settings.RxGain - 1.0) > 1e-6)
				Resampler.ApplyGain(outgoing, _settings.RxGain);
			_limiter.Process(outgoing);

			_publishQueue.Writer.TryWrite(outgoing);

			try
			{
				RadioAudioForwarded?.Invoke(this, outgoing);
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Radio audio subscriber failed");
			}
		}

		private void ReportRxLevel(short[] frame, bool squelchOpen)
		{
			var handler = RxLevelSampled;
			if (handler == null)
				return;

			var now = DateTime.UtcNow.Ticks;
			if (now - _lastRxLevelTicks < RxLevelIntervalTicks)
				return;
			_lastRxLevelTicks = now;

			try
			{
				handler(this, new RadioRxLevel(AudioFormat.Dbfs(frame), squelchOpen));
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Radio level subscriber failed");
			}
		}

		private bool IsSquelchOpen(short[] frame)
		{
			var mode = _settings.Squelch.Mode;
			if (mode == SquelchMode.Carrier)
				return _carrier.IsCarrierPresent;

			// After our own transmission: the reset is consumed on this frame whenever it comes (pending first, then the
			// guard: the unkey sets them in the opposite order), and frames inside the guard are discarded as well.
			var reset = Volatile.Read(ref _squelchResetPending) != 0 && Interlocked.Exchange(ref _squelchResetPending, 0) != 0;
			var inGuard = InSquelchGuard();
			if (reset || inGuard)
				ResetSquelch(mode);
			if (inGuard)
				return false;

			switch (mode)
			{
				case SquelchMode.Off:
					// Off is for radios with hardware squelch: while it is closed the radio passes (near) silence. Treating every
					// frame as open held Receiving permanently, so with AntiLoop the radio was never keyed, the RX lamp stayed lit
					// and silence streamed to the channel; only audible audio counts, held over speech pauses by the squelch hang.
					return IsHardwareSquelchedAudioPresent(frame);
				case SquelchMode.Ctcss:
					return _ctcss.Process(frame);
				case SquelchMode.Vox:
				default:
					return _squelch.Process(frame);
			}
		}

		// Capture thread only: closes whichever gate the mode uses and clears its hang.
		private void ResetSquelch(SquelchMode mode)
		{
			switch (mode)
			{
				case SquelchMode.Off:
					_offHangRemainingMs = 0;
					break;
				case SquelchMode.Ctcss:
					_ctcss.Reset();
					break;
				case SquelchMode.Vox:
				default:
					_squelch.ForceClosed();
					break;
			}
		}

		private bool InSquelchGuard() => DateTime.UtcNow.Ticks < Interlocked.Read(ref _squelchGuardUntilTicks);

		// Squelch Off: the frame carries audio (at/above OpenDbfs), or follows one by less than the squelch hang. The hang is
		// counted in captured audio, like SquelchGate's, so it does not depend on when the capture thread gets to run.
		private bool IsHardwareSquelchedAudioPresent(short[] frame)
		{
			if (AudioFormat.Dbfs(frame) >= _settings.Squelch.OpenDbfs)
			{
				_offHangRemainingMs = Math.Max(0, _settings.Squelch.HangMs);
				return true;
			}

			if (_offHangRemainingMs <= 0)
				return false;

			_offHangRemainingMs -= frame.Length * 1000.0 / AudioFormat.SampleRate;
			return _offHangRemainingMs > 0;
		}

		private async Task PublishPumpAsync(CancellationToken token)
		{
			var consecutiveFailures = 0;
			long firstFailureTicks = 0;
			long lastWarningTicks = 0;
			try
			{
				await foreach (var frame in _publishQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
				{
					var publisher = _publisher;
					if (publisher == null)
						continue;

					// One failed frame must not end publishing for the rest of the session (the pump used to exit on the first
					// exception, leaving the radio side silent on the channel while the mode reported Running).
					try
					{
						await WriteFrameAsync(publisher, frame, token).ConfigureAwait(false);
						if (consecutiveFailures > 0)
						{
							_logger?.Information("Publishing radio audio to the channel recovered after {Failures} failed frame(s)", consecutiveFailures);
							consecutiveFailures = 0;
						}
					}
					catch (OperationCanceledException) when (token.IsCancellationRequested)
					{
						throw;
					}
					catch (Exception ex)
					{
						var now = DateTime.UtcNow.Ticks;
						if (consecutiveFailures++ == 0)
							firstFailureTicks = now;

						if (lastWarningTicks == 0 || now - lastWarningTicks >= PublishWarnIntervalTicks)
						{
							lastWarningTicks = now;
							_logger?.Warning(ex, "Failed to publish radio audio to the channel ({Failures} consecutive); dropping the frame", consecutiveFailures);
						}

						var failingFor = TimeSpan.FromTicks(now - firstFailureTicks);
						if (consecutiveFailures > MaxConsecutivePublishFailures || (consecutiveFailures > 1 && failingFor >= PublishFailureWindow))
							RaiseFault($"Publishing radio audio to the channel keeps failing ({consecutiveFailures} frames over {failingFor.TotalSeconds:0.0} s)", ex);
					}
				}
			}
			catch (OperationCanceledException) { /* shutting down */ }
			catch (Exception ex)
			{
				_logger?.Error(ex, "Radio publish pump failed");
				RaiseFault("Radio publish pump failed", ex);
			}
		}

		// One publish write, bounded by PublishWriteTimeout: a write still pending then is abandoned (and asked to stop) and
		// reported as a TimeoutException, which the pump counts as a failed frame. The bridge's token still ends it at once.
		private static async Task WriteFrameAsync(IAudioPublisher publisher, short[] frame, CancellationToken token)
		{
			using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
			writeCts.CancelAfter(PublishWriteTimeout);
			try
			{
				await publisher.WriteAsync(frame, writeCts.Token).AsTask().WaitAsync(PublishWriteTimeout, token).ConfigureAwait(false);
			}
			catch (Exception ex) when (!token.IsCancellationRequested && (ex is TimeoutException || (ex is OperationCanceledException && writeCts.IsCancellationRequested)))
			{
				try { writeCts.Cancel(); } catch { /* ignore */ }
				throw new TimeoutException($"Publishing a radio audio frame did not complete within {PublishWriteTimeout.TotalSeconds:0} s", ex);
			}
		}

		// ---- Channel → radio transmit --------------------------------------------

		private void OnChannelAudio(object sender, VoiceAudioFrame frame)
		{
			if (frame?.Pcm == null || frame.Pcm.Length == 0)
				return;

			if (Fault != null)
				return;

			// Half-duplex: don't transmit while the radio is actively receiving.
			if (_settings.AntiLoop && _receiving)
				return;

			var audible = AudioFormat.Dbfs(frame.Pcm) >= TxActivityDbfs;
			var talker = frame.TrackSid ?? frame.Participant?.ToString() ?? "remote";
			var nowTicks = DateTime.UtcNow.Ticks;
			var talkerHangTicks = TimeSpan.FromMilliseconds(Math.Max(_settings.TxHangMs, 500)).Ticks;

			long turn;
			lock (_talkerLock)
			{
				if (_activeTalker != null && nowTicks - _activeTalkerLastAudibleTicks > talkerHangTicks)
				{
					_activeTalker = null;
					_floorTurn++;
					// A talker cut off by the transmit time-out has finally gone quiet; the next one may key the radio.
					_timeOutLockout = false;
				}

				if (_activeTalker == null)
				{
					if (!audible)
						return;

					_activeTalker = talker;
					_activeTalkerSinceTicks = nowTicks;
					_floorTurn++;
				}
				else if (!String.Equals(_activeTalker, talker, StringComparison.Ordinal))
				{
					// Someone else has the floor: one stream at a time reaches the radio.
					return;
				}

				if (audible)
					_activeTalkerLastAudibleTicks = nowTicks;

				// Transmit time-out: this talker keeps the floor (so nobody else keys up over them) but is not transmitted until
				// they have been quiet for the talker hang.
				if (_timeOutLockout)
					return;

				turn = _floorTurn;
			}

			var tx = (short[])frame.Pcm.Clone();
			if (Math.Abs(_settings.TxGain - 1.0) > 1e-6)
				Resampler.ApplyGain(tx, _settings.TxGain);
			_limiter.Process(tx);

			bool changed;
			lock (_txLock)
			{
				changed = false;

				// Checked again here: the time-out, a fault's unkey and the shutdown's final release all happen under _txLock,
				// and a frame that passed the checks above just before one of them must not key the radio straight back up.
				if (_stopping || Fault != null)
					return;

				// So is the floor. A frame held up between the two locks for longer than the talker hang must not key the radio,
				// or queue audio, for a talker who has since lost the floor. Rechecked under _txLock (nested in the usual order),
				// so the floor can only change hands at the radio between frames: whoever takes it next keys and queues through
				// this same lock, after this frame.
				lock (_talkerLock)
				{
					if (_timeOutLockout || _floorTurn != turn)
						return;
				}

				if (!_wantKeyed)
				{
					// Silence never keys the radio.
					if (!audible)
						return;

					changed = KeyUp(ref nowTicks, frame.Participant?.ToString() ?? "remote");
				}
				else if (!audible && nowTicks - _lastTxTicks >= TimeSpan.TicksPerMillisecond * Math.Max(0, _settings.TxHangMs))
				{
					// The talker's pauses are passed through while the radio is keyed (so speech keeps its timing), but once they
					// have been quiet past the hang their silence is dropped, so the queue drains and the tail can unkey.
					return;
				}

				QueueTransmit(tx, nowTicks);
				if (audible)
				{
					_lastTxTicks = nowTicks;
					// Speech resumed after the courtesy beep went out: beep again at the real end.
					_courtesyPlayed = false;
				}
			}

			if (changed)
				RaiseTxRxChanged();
		}

		// Under _txLock. Returns whether Transmitting changed; nowTicks is advanced to when the line was driven.
		private bool KeyUp(ref long nowTicks, string who)
		{
			// The new transmission's state is recorded before the line is driven, so the tail logic never sees a keyed radio
			// with the previous transmission's (long expired) idle time.
			_courtesyPlayed = false;
			_lastTxTicks = nowTicks;
			_keyedAtTicks = nowTicks;
			_txQueuedUntilTicks = nowTicks;
			_wantKeyed = true;

			// A line still keyed after a failed unkey is keyed for this transmission too.
			var keyed = _ptt.Key() || _ptt.IsKeyed;

			// The transmission starts once the line is up (a HID/serial write can take a while): time it from there.
			nowTicks = Math.Max(nowTicks, DateTime.UtcNow.Ticks);
			_lastTxTicks = nowTicks;
			_keyedAtTicks = nowTicks;
			_txQueuedUntilTicks = nowTicks;
			_deviceDrainedAtTicks = nowTicks;

			if (keyed)
			{
				_pttMismatchSinceTicks = 0;
				_keyPending = false;
			}
			else
			{
				_pttMismatchSinceTicks = nowTicks;
				_lastPttRetryTicks = nowTicks;
				_keyPending = true;
				_logger?.Warning("PTT key failed; holding the transmit audio and retrying");
			}
			_logger?.Debug("PTT keyed (channel audio from {Who})", who);

			// The device plays its queue in real time, so audio queued behind this silence reaches the radio _txLeadMs
			// after key-up, and stays that far behind for the rest of the transmission. After a failed key it is queued once
			// the line does key (StartHeldTransmit).
			if (_txLeadMs > 0 && !_keyPending)
				QueueTransmit(_tones.Silence(_txLeadMs), nowTicks);

			var changed = UpdateTransmitting(true);
			// Receive is now held off by Transmitting itself (lifted only after it is set, so there is no gap between the two).
			_unkeyPending = false;
			return changed;
		}

		// Under _txLock.
		private void QueueTransmit(short[] pcm, long nowTicks)
		{
			if (_keyPending)
			{
				HoldTransmit(pcm);
				return;
			}

			_device.Transmit(pcm);
			var startTicks = Math.Max(_txQueuedUntilTicks, nowTicks);
			_txQueuedUntilTicks = startTicks + pcm.Length * TimeSpan.TicksPerSecond / AudioFormat.SampleRate;
		}

		// Under _txLock, while the key-up waits for the line: keeps the most recent MaxHeldTransmitSamples of audio.
		private void HoldTransmit(short[] pcm)
		{
			_heldTransmit.Enqueue(pcm);
			_heldTransmitSamples += pcm.Length;
			while (_heldTransmitSamples > MaxHeldTransmitSamples && _heldTransmit.Count > 1)
				_heldTransmitSamples -= _heldTransmit.Dequeue().Length;
		}

		// Under _txLock: the line has keyed after a failed key-up. The transmission starts now: the lead runs from here, then
		// the held audio plays, and the live audio follows it.
		private void StartHeldTransmit(long now)
		{
			_keyPending = false;
			_keyedAtTicks = now;
			_txQueuedUntilTicks = now;
			_deviceDrainedAtTicks = now;

			_logger?.Debug("Transmitting the {Ms:0} ms of channel audio held while the PTT line keyed",
				_heldTransmitSamples * 1000.0 / AudioFormat.SampleRate);
			if (_txLeadMs > 0)
				QueueTransmit(_tones.Silence(_txLeadMs), now);
			while (_heldTransmit.Count > 0)
				QueueTransmit(_heldTransmit.Dequeue(), now);
			_heldTransmitSamples = 0;
		}

		// Under _txLock.
		private void DropHeldTransmit()
		{
			_keyPending = false;
			_heldTransmit.Clear();
			_heldTransmitSamples = 0;
		}

		// Under _txLock, on a forced unkey (the transmit time-out, a fault): throws away the transmit audio still queued. A
		// cut-off talker's backlog (seconds of it behind a lead) would otherwise play into whoever keys the radio next, ahead
		// of their own lead and speech; and with VOX keying the queued audio itself holds the radio on the air. What the
		// output device has already taken (its OutputLatency) plays out within the talker hang, before anyone can key up.
		private void DiscardQueuedTransmit(long now)
		{
			DropHeldTransmit();
			_txQueuedUntilTicks = now;
			_deviceDrainedAtTicks = now;
			try
			{
				_device.ClearTransmit();
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Failed to discard the queued transmit audio");
			}
		}

		// Under _txLock. Returns whether Transmitting changed.
		private bool ReleaseTransmit(string reason, long nowTicks)
		{
			_wantKeyed = false;
			DropHeldTransmit();

			// Unkeyed too if the write failed on a line that was never keyed (a key-up whose own write failed).
			if (_ptt.Unkey() || !_ptt.IsKeyed)
			{
				_pttMismatchSinceTicks = 0;
				OnUnkeyConfirmed(nowTicks);
			}
			else
			{
				if (_pttMismatchSinceTicks == 0)
					_pttMismatchSinceTicks = nowTicks;
				_lastPttRetryTicks = nowTicks;
				// Receive stays suppressed (and the squelch guard waits) until the line has actually released.
				_unkeyPending = true;
				_logger?.Warning("PTT unkey failed ({Reason}); retrying", reason);
			}

			_logger?.Debug("PTT unkeyed ({Reason}) after {Ms:0} ms keyed", reason, TimeSpan.FromTicks(nowTicks - _keyedAtTicks).TotalMilliseconds);
			return UpdateTransmitting(false);
		}

		// Under _txLock: the PTT line is confirmed released. Receive audio is let through again, behind the squelch guard
		// (published before the suppression is lifted, so the capture thread never sees neither).
		private void OnUnkeyConfirmed(long nowTicks)
		{
			if (_settings.AntiLoop)
			{
				Interlocked.Exchange(ref _squelchGuardUntilTicks, nowTicks + SquelchGuardTicks);
				Interlocked.Exchange(ref _squelchResetPending, 1);
			}
			_unkeyPending = false;
		}

		private void OnTxTimer()
		{
			// A timer callback that throws takes the process down.
			try
			{
				ManageTransmitTail();
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Radio transmit tail check failed");
			}
		}

		private void ManageTransmitTail()
		{
			bool changed = false;
			string fault = null;
			lock (_txLock)
			{
				var now = DateTime.UtcNow.Ticks;
				fault = SyncPttLine(now);

				var buffered = Math.Max(0, _device.BufferedTransmit.Ticks);
				fault ??= CheckTransmitOutput(now, buffered, _device.PlayedTransmit.Ticks);

				if (_wantKeyed && fault == null)
					changed = ManageKeyedTail(now, buffered);
			}

			if (changed)
				RaiseTxRxChanged();
			if (fault != null)
				RaiseFault(fault);
		}

		// Under _txLock. Re-drives a PTT line that a failed write left out of step with the bridge; returns a fault reason when
		// it has stayed out of step too long.
		private string SyncPttLine(long now)
		{
			if (_ptt.IsKeyed == _wantKeyed)
			{
				if (_pttMismatchSinceTicks != 0)
				{
					_pttMismatchSinceTicks = 0;
					_logger?.Information("PTT line back in step ({State})", _wantKeyed ? "keyed" : "unkeyed");
				}
				OnPttLineInStep(now);
				return null;
			}

			if (_pttMismatchSinceTicks == 0)
				_pttMismatchSinceTicks = now;

			if (now - _lastPttRetryTicks >= PttRetryTicks)
			{
				_lastPttRetryTicks = now;
				var ok = _wantKeyed ? _ptt.Key() : _ptt.Unkey();
				if (ok && _ptt.IsKeyed == _wantKeyed)
				{
					_pttMismatchSinceTicks = 0;
					_logger?.Information("PTT line {State} after retrying", _wantKeyed ? "keyed" : "unkeyed");
					// Timed from when the write returned (a HID/serial write can take a while).
					OnPttLineInStep(Math.Max(now, DateTime.UtcNow.Ticks));
					return null;
				}
			}

			if (now - _pttMismatchSinceTicks >= PttFaultAfterTicks)
				return $"PTT line not responding: could not {(_wantKeyed ? "key" : "unkey")} the radio for {TimeSpan.FromTicks(now - _pttMismatchSinceTicks).TotalSeconds:0} s";

			return null;
		}

		// Under _txLock: the PTT line is in the state the bridge wants. A held key-up starts transmitting; an unconfirmed
		// release lets receive audio through again.
		private void OnPttLineInStep(long now)
		{
			if (_wantKeyed)
			{
				if (_keyPending)
					StartHeldTransmit(now);
			}
			else if (_unkeyPending)
			{
				OnUnkeyConfirmed(now);
			}
		}

		// Under _txLock. Returns a fault reason once the device has stopped playing its transmit backlog: the output plays
		// continuously whether or not PTT is keyed, so a backlog it is not playing from means the device stopped pulling audio
		// without reporting it. Progress is the device's own playback counter (a talker refilling the queue as fast as it
		// plays can hold the backlog at one depth on every tick), or, for a device that cannot report one, the backlog
		// shrinking. Checked while unkeyed too, since the bounded drain wait can unkey first.
		private string CheckTransmitOutput(long now, long buffered, long played)
		{
			var progressed = played != _lastDevicePlayedTicks;
			_lastDevicePlayedTicks = played;

			if (buffered <= 0)
			{
				_backlogStuckSinceTicks = 0;
				_lastDeviceBacklogTicks = 0;
				return null;
			}

			if (_backlogStuckSinceTicks == 0 || progressed || buffered < _lastDeviceBacklogTicks)
				_backlogStuckSinceTicks = now; // just queued, or playing out
			_lastDeviceBacklogTicks = buffered;

			var stuckFor = now - _backlogStuckSinceTicks;
			if (stuckFor < TransmitStallFaultTicks)
				return null;

			return $"Transmit output stalled: the audio device has not played any of its {TimeSpan.FromTicks(buffered).TotalMilliseconds:0} ms transmit backlog for {TimeSpan.FromTicks(stuckFor).TotalSeconds:0.0} s";
		}

		// Under _txLock, while keyed. Returns whether Transmitting changed.
		private bool ManageKeyedTail(long now, long buffered)
		{
			// Not on the air yet (the key-up's PTT write failed): nothing has been queued to time a tail from. The line keys on
			// a retry (StartHeldTransmit), or the PTT fault ends the transmission.
			if (_keyPending)
				return false;

			// When the device's own queue empties (or will): pushed out while it reports a backlog, pulled in to now once it
			// reports none. Never more than MaxDrainOverrun past the bridge's own estimate: the device holds only what the bridge
			// queued, so a backlog beyond that is a device that has stopped playing, not audio still to come.
			if (buffered > 0)
				_deviceDrainedAtTicks = Math.Min(now + buffered, _txQueuedUntilTicks + MaxDrainOverrunTicks);
			else if (_deviceDrainedAtTicks > now)
				_deviceDrainedAtTicks = now;

			var maxTransmitSeconds = Math.Max(0, _settings.MaxTransmitSeconds);
			if (maxTransmitSeconds > 0)
			{
				// Timed from when the current talker took the floor (or from key-up, if later): the radio can stay keyed across a
				// quick back-and-forth (a PoC lead keeps it keyed for seconds), and timing from key-up cut off whoever happened to
				// be talking once the conversation as a whole passed the limit. Decided and locked out under one _talkerLock, so a
				// talker who takes the floor meanwhile is not locked out for their predecessor's turn.
				string talker = null;
				var timedOut = false;
				lock (_talkerLock)
				{
					var since = Math.Max(_keyedAtTicks, _activeTalkerSinceTicks);
					if (now - since >= maxTransmitSeconds * TimeSpan.TicksPerSecond)
					{
						timedOut = true;
						talker = _activeTalker;
						if (_activeTalker != null)
							_timeOutLockout = true;
					}
				}

				if (timedOut)
				{
					_logger?.Warning("Transmit time-out: {Talker} has held the radio keyed for {Seconds} s; unkeying and ignoring them until they stop talking",
						talker ?? "the channel", maxTransmitSeconds);
					// Unkeyed first: discarding the cut-off audio must never be what stands between a time-out and the unkey.
					var changed = ReleaseTransmit("transmit time-out", now);
					DiscardQueuedTransmit(now);
					return changed;
				}
			}

			var idleTicks = now - _lastTxTicks;
			if (idleTicks < TimeSpan.TicksPerMillisecond * Math.Max(0, _settings.TxHangMs))
				return false;

			if (_settings.CourtesyTone && !_courtesyPlayed)
			{
				// Queues behind the backlog (lead, buffered speech), so it plays at the right point.
				QueueTransmit(_tones.CourtesyBeep(), now);
				_courtesyPlayed = true;
				return false;
			}

			// Unkey only once everything queued has played out: the later of the bridge's own estimate of the queue (which
			// covers devices that cannot report one) and the device's reported backlog (bounded, above), then the device's output
			// latency, then the tail. Unkeying on idle time alone dropped PTT while the courtesy beep / last syllable was still in the output
			// buffers.
			var drainedAt = Math.Max(_txQueuedUntilTicks, _deviceDrainedAtTicks);
			var unkeyAt = drainedAt + Math.Max(0, _device.OutputLatency.Ticks) + TimeSpan.TicksPerMillisecond * Math.Max(0, _settings.TxTailMs);
			if (now < unkeyAt)
				return false;

			return ReleaseTransmit($"{TimeSpan.FromTicks(idleTicks).TotalMilliseconds:0} ms idle", now);
		}

		private void OnDeviceFaulted(object sender, RadioFaultEventArgs e) =>
			RaiseFault($"Radio audio device failed: {e?.Reason ?? "unknown"}", e?.Exception);

		private void RaiseFault(string reason, Exception exception = null)
		{
			var fault = new RadioFaultEventArgs(reason, exception);
			if (Interlocked.CompareExchange(ref _fault, fault, null) != null)
				return;

			_logger?.Error(exception, "Radio bridge fault: {Reason}", reason);

			// Don't hold the transmitter on a path that no longer carries audio: also when the bridge had already released but
			// the line did not follow (a failed unkey), and whatever is still queued (with VOX keying it keys the radio). The
			// tail timer keeps re-driving a line that stays keyed until the owner tears the bridge down.
			bool changed = false;
			try
			{
				lock (_txLock)
				{
					var now = DateTime.UtcNow.Ticks;
					if (_wantKeyed || _unkeyPending || _ptt.IsKeyed)
						changed = ReleaseTransmit("fault", now);
					DiscardQueuedTransmit(now);
				}
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Failed to unkey the radio after a fault");
			}
			if (changed)
				RaiseTxRxChanged();

			// Off the raising thread (capture/playback, the publish pump, the tail timer): the owner's handler tears the bridge
			// down, which must not run inside those.
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					Faulted?.Invoke(this, fault);
				}
				catch (Exception ex)
				{
					_logger?.Warning(ex, "Radio fault subscriber failed");
				}
			});
		}

		private void OnMdcPacket(object sender, Mdc1200Packet packet)
		{
			_logger?.Information("MDC-1200 decoded: {Packet}", packet);
			if (packet.IsEmergency(new Mdc1200Settings()))
				RaiseEmergency("MDC-1200", $"Emergency from unit {packet.UnitId:X4} (op 0x{packet.Op:X2})");
		}

		private void RaiseEmergency(string source, string detail)
		{
			if (_alertSink == null)
				return;
			_ = _alertSink.RaiseAsync(source, detail);
		}

		public async ValueTask DisposeAsync()
		{
			try { _cts?.Cancel(); } catch { /* ignore */ }
			_publishQueue.Writer.TryComplete();

			// The bridge is gone: nothing is being transmitted or received any more. Fenced under _txLock before anything else:
			// unsubscribing does not stop a channel callback already in flight, and one that reached _txLock after the final
			// unkey would key the radio again with no tail timer left to release it. Callbacks that got there first are
			// followed by this release; those that get there after it see _stopping and drop their audio.
			bool changed;
			bool released;
			lock (_txLock)
			{
				_stopping = true;
				_wantKeyed = false;
				DropHeldTransmit();
				released = TryUnkey();
				changed = UpdateTransmitting(false);
			}

			if (_session != null)
				_session.AudioFrameReceived -= OnChannelAudio;
			_device.SamplesReceived -= OnRadioAudio;
			_device.Faulted -= OnDeviceFaulted;

			if (_txTimer != null)
				await _txTimer.DisposeAsync().ConfigureAwait(false);

			// The tail timer used to re-drive a line a failed write left keyed; it is gone now, so retry here (briefly).
			for (int attempt = 0; !released && attempt < FinalUnkeyRetries; attempt++)
			{
				await Task.Delay(FinalUnkeyRetryDelay).ConfigureAwait(false);
				lock (_txLock)
					released = TryUnkey();
			}
			if (!released)
				_logger?.Error("Radio bridge stopped but the PTT line could not be released: the radio may still be transmitting. Check or disconnect the radio interface.");

			if (_receiving)
			{
				_receiving = false;
				changed = true;
			}
			if (changed)
				RaiseTxRxChanged();

			// Bounded: a publish write that ignores cancellation must not hang teardown (the pump itself gives up on it at once).
			var pump = _publishPump;
			if (pump != null && !await WaitBoundedAsync(() => pump).ConfigureAwait(false))
				_logger?.Warning("Radio publish pump did not stop within {Seconds} s; abandoning it", DisposeWaitTimeout.TotalSeconds);
			var publisher = _publisher;
			if (publisher != null && !await WaitBoundedAsync(() => publisher.DisposeAsync().AsTask()).ConfigureAwait(false))
				_logger?.Warning("Radio channel publisher did not close within {Seconds} s; abandoning it", DisposeWaitTimeout.TotalSeconds);

			_device.StopTransmit();
			_device.StopReceive();
			_cts?.Dispose();
		}

		// Under _txLock (or once nothing else can drive the line). True when the line is released, as far as the controller knows.
		private bool TryUnkey()
		{
			try
			{
				return _ptt.Unkey() || !_ptt.IsKeyed;
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "PTT unkey threw");
				return false;
			}
		}

		// Waits for a teardown step for at most DisposeWaitTimeout. False when it is still running then; its failure is ignored.
		private async Task<bool> WaitBoundedAsync(Func<Task> step)
		{
			try
			{
				await step().WaitAsync(DisposeWaitTimeout).ConfigureAwait(false);
				return true;
			}
			catch (TimeoutException)
			{
				return false;
			}
			catch (Exception ex)
			{
				_logger?.Debug(ex, "Radio bridge teardown step failed");
				return true;
			}
		}
	}
}
