using System;
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
		private long _lastTxTicks;
		private bool _courtesyPlayed;

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
			_ctcss = new CtcssDetector(settings.Squelch.CtcssFrequency, settings.Squelch.CtcssMinStrength, AudioFormat.SampleRate);
			_highPass = new HighPassFilter(Math.Max(1, settings.HighPassCutoffHz), AudioFormat.SampleRate);

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

			_publisher = await _session.CreatePublisherAsync("radio", cancellationToken).ConfigureAwait(false);
			_publishPump = Task.Run(() => PublishPumpAsync(_cts.Token));

			_session.AudioFrameReceived += OnChannelAudio;
			_device.SamplesReceived += OnRadioAudio;
			_device.StartReceive();
			_device.StartTransmit();

			_txTimer = new Timer(_ => ManageTransmitTail(), null, 50, 50);
			_logger?.Information("Radio bridge running: {Channel} <-> radio (squelch={Mode}, ptt={Ptt})",
				_session.ChannelName, _settings.Squelch.Mode, _settings.Ptt);
		}

		// ---- Radio receive → channel ---------------------------------------------

		private void OnRadioAudio(object sender, short[] frame)
		{
			if (frame == null || frame.Length == 0)
				return;

			// Always feed the signaling decoders (they apply their own thresholds).
			_mdc?.Process(frame);
			_emergency?.Process(frame);

			bool open = IsSquelchOpen(frame);
			_receiving = open;

			if (!open || (_settings.AntiLoop && _transmitting))
				return;

			// Copy + condition the audio before publishing.
			var outgoing = (short[])frame.Clone();
			_highPass.Process(outgoing);
			if (Math.Abs(_settings.RxGain - 1.0) > 1e-6)
				Resampler.ApplyGain(outgoing, _settings.RxGain);
			_limiter.Process(outgoing);

			_publishQueue.Writer.TryWrite(outgoing);
		}

		private bool IsSquelchOpen(short[] frame)
		{
			switch (_settings.Squelch.Mode)
			{
				case SquelchMode.Off: return true;
				case SquelchMode.Carrier: return _carrier.IsCarrierPresent;
				case SquelchMode.Ctcss: return _ctcss.Process(frame);
				case SquelchMode.Vox:
				default: return _squelch.Process(frame);
			}
		}

		private async Task PublishPumpAsync(CancellationToken token)
		{
			try
			{
				await foreach (var frame in _publishQueue.Reader.ReadAllAsync(token).ConfigureAwait(false))
				{
					if (_publisher != null)
						await _publisher.WriteAsync(frame, token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) { /* shutting down */ }
			catch (Exception ex) { _logger?.Error(ex, "Radio publish pump failed"); }
		}

		// ---- Channel → radio transmit --------------------------------------------

		private void OnChannelAudio(object sender, VoiceAudioFrame frame)
		{
			if (frame?.Pcm == null || frame.Pcm.Length == 0)
				return;

			// Half-duplex: don't transmit while the radio is actively receiving.
			if (_settings.AntiLoop && _receiving)
				return;

			if (!_ptt.IsKeyed)
			{
				_ptt.Key();
				_transmitting = true;
				_courtesyPlayed = false;
				_logger?.Debug("PTT keyed (channel audio from {Who})", frame.Participant?.ToString() ?? "remote");
			}

			var tx = (short[])frame.Pcm.Clone();
			if (Math.Abs(_settings.TxGain - 1.0) > 1e-6)
				Resampler.ApplyGain(tx, _settings.TxGain);
			_limiter.Process(tx);

			_device.Transmit(tx);
			Interlocked.Exchange(ref _lastTxTicks, DateTime.UtcNow.Ticks);
		}

		private void ManageTransmitTail()
		{
			if (!_ptt.IsKeyed)
				return;

			var idleMs = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastTxTicks), DateTimeKind.Utc)).TotalMilliseconds;

			if (idleMs >= _settings.TxHangMs && !_courtesyPlayed && _settings.CourtesyTone)
			{
				_device.Transmit(_tones.CourtesyBeep());
				_courtesyPlayed = true;
				return;
			}

			if (idleMs >= _settings.TxHangMs + _settings.TxTailMs)
			{
				_ptt.Unkey();
				_transmitting = false;
				_logger?.Debug("PTT unkeyed after {Idle:0} ms idle", idleMs);
			}
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

			if (_session != null)
				_session.AudioFrameReceived -= OnChannelAudio;
			_device.SamplesReceived -= OnRadioAudio;

			if (_txTimer != null)
				await _txTimer.DisposeAsync().ConfigureAwait(false);

			try { _ptt.Unkey(); } catch { /* ignore */ }

			try { if (_publishPump != null) await _publishPump.ConfigureAwait(false); } catch { /* ignore */ }
			if (_publisher != null)
				await _publisher.DisposeAsync().ConfigureAwait(false);

			_device.StopTransmit();
			_device.StopReceive();
			_cts?.Dispose();
		}
	}
}
