using System;
using System.Collections.Generic;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>
	/// Streaming MDC-1200 decoder. Buffers inbound RF audio and periodically attempts
	/// to decode a packet, de-duplicating repeats. Feed it PCM16 mono frames from the
	/// radio receive path.
	/// </summary>
	public sealed class Mdc1200Decoder
	{
		private readonly Mdc1200Settings _settings;
		private readonly int _sampleRate;
		private readonly int _windowSamples;
		private readonly int _tailSamples;
		private readonly List<short> _buffer = new List<short>();

		private string _lastPacket;
		private DateTime _lastPacketUtc;

		public event EventHandler<Mdc1200Packet> PacketDecoded;

		public Mdc1200Decoder(Mdc1200Settings settings, int sampleRate)
		{
			_settings = settings ?? new Mdc1200Settings();
			_sampleRate = sampleRate;

			// One packet is ~ (preamble + 16 sync + 48 payload) bits. Window generously.
			int samplesPerBit = sampleRate / _settings.Baud;
			int packetBits = _settings.PreambleBits + 16 + 48;
			_windowSamples = (int)(packetBits * samplesPerBit * 1.5);
			_tailSamples = packetBits * samplesPerBit; // overlap so packets aren't split
		}

		public void Process(ReadOnlySpan<short> pcm)
		{
			for (int i = 0; i < pcm.Length; i++)
				_buffer.Add(pcm[i]);

			if (_buffer.Count < _windowSamples)
				return;

			var window = _buffer.ToArray();
			var packet = Mdc1200Codec.TryDecode(window, _settings, _sampleRate);
			if (packet != null)
				Emit(packet);

			// Retain a tail so a packet spanning the boundary still decodes next time.
			int drop = _buffer.Count - _tailSamples;
			if (drop > 0)
				_buffer.RemoveRange(0, drop);
		}

		private void Emit(Mdc1200Packet packet)
		{
			var key = packet.ToString();
			var now = DateTime.UtcNow;
			if (key == _lastPacket && (now - _lastPacketUtc).TotalSeconds < 2)
				return;

			_lastPacket = key;
			_lastPacketUtc = now;
			PacketDecoded?.Invoke(this, packet);
		}
	}
}
