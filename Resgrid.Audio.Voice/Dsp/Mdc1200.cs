using System;
using System.Collections.Generic;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>
	/// MDC-1200 (Motorola) data burst parameters. MDC-1200 is 1200 bps FFSK with a
	/// 1200 Hz mark / 1800 Hz space, a preamble, a sync word, then op/arg/unit-id and a
	/// CRC. Real off-air signals vary in polarity/preamble/sync between fleets, so the
	/// tone polarity and sync word are configurable; defaults match the common case.
	/// </summary>
	public sealed class Mdc1200Settings
	{
		public double MarkHz { get; set; } = 1200;   // bit '1'
		public double SpaceHz { get; set; } = 1800;   // bit '0'
		public int Baud { get; set; } = 1200;
		public int PreambleBits { get; set; } = 24;   // alternating 1010… for clock lock
		public ushort SyncWord { get; set; } = 0x0707;
		/// <summary>Op-code that denotes an emergency (radio emergency button / ANI).</summary>
		public byte EmergencyOp { get; set; } = 0x06;
	}

	/// <summary>A decoded MDC-1200 packet.</summary>
	public sealed class Mdc1200Packet
	{
		public byte Op { get; set; }
		public byte Arg { get; set; }
		public ushort UnitId { get; set; }

		public bool IsEmergency(Mdc1200Settings settings) => Op == settings.EmergencyOp;

		public override string ToString() => $"MDC1200 op=0x{Op:X2} arg=0x{Arg:X2} unit={UnitId:X4}";
	}

	/// <summary>
	/// MDC-1200 FFSK encode/decode helpers. The encoder is primarily used to generate
	/// known signals for unit tests (so the decoder can be validated by round-trip) and
	/// for any future "transmit ANI" feature. CRC is CRC-16/CCITT (poly 0x1021, init
	/// 0xFFFF) over the four payload bytes.
	/// </summary>
	public static class Mdc1200Codec
	{
		/// <summary>Generates a continuous-phase FFSK waveform (PCM16 mono) for a packet.</summary>
		public static short[] Encode(Mdc1200Packet packet, Mdc1200Settings settings, int sampleRate, double amplitude = 0.6)
		{
			var bits = BuildBitStream(packet, settings);
			int samplesPerBit = sampleRate / settings.Baud;
			var samples = new short[bits.Count * samplesPerBit];

			double phase = 0;
			int idx = 0;
			foreach (var bit in bits)
			{
				double freq = bit ? settings.MarkHz : settings.SpaceHz;
				double inc = 2 * Math.PI * freq / sampleRate;
				for (int s = 0; s < samplesPerBit; s++)
				{
					samples[idx++] = (short)(Math.Sin(phase) * amplitude * short.MaxValue);
					phase += inc;
					if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
				}
			}
			return samples;
		}

		/// <summary>The bits transmitted: preamble + sync + op,arg,unitHi,unitLo + CRC16.</summary>
		public static List<bool> BuildBitStream(Mdc1200Packet packet, Mdc1200Settings settings)
		{
			var bits = new List<bool>();
			for (int i = 0; i < settings.PreambleBits; i++)
				bits.Add(i % 2 == 0);

			AppendBits(bits, settings.SyncWord, 16);

			var payload = new byte[] { packet.Op, packet.Arg, (byte)(packet.UnitId >> 8), (byte)(packet.UnitId & 0xFF) };
			foreach (var b in payload)
				AppendBits(bits, b, 8);

			ushort crc = Crc16Ccitt(payload);
			AppendBits(bits, crc, 16);
			return bits;
		}

		public static void AppendBits(List<bool> bits, int value, int count)
		{
			for (int i = count - 1; i >= 0; i--)
				bits.Add(((value >> i) & 1) == 1);
		}

		public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
		{
			ushort crc = 0xFFFF;
			foreach (var b in data)
			{
				crc ^= (ushort)(b << 8);
				for (int i = 0; i < 8; i++)
					crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
			}
			return crc;
		}

		/// <summary>
		/// Attempts to decode a single MDC-1200 packet from a PCM16 buffer by trying all
		/// bit-phase alignments, locating the sync word, then verifying the CRC.
		/// Returns null if no valid packet is present.
		/// </summary>
		public static Mdc1200Packet TryDecode(ReadOnlySpan<short> pcm, Mdc1200Settings settings, int sampleRate)
		{
			int samplesPerBit = sampleRate / settings.Baud;
			if (pcm.Length < samplesPerBit * (16 + 48))
				return null;

			// Copy to array so we can re-window across phase offsets.
			var samples = pcm.ToArray();

			for (int phase = 0; phase < samplesPerBit; phase++)
			{
				var bits = SliceBits(samples, phase, samplesPerBit, settings, sampleRate);
				var packet = FindPacket(bits, settings);
				if (packet != null)
					return packet;
			}
			return null;
		}

		private static bool[] SliceBits(short[] samples, int phase, int samplesPerBit, Mdc1200Settings settings, int sampleRate)
		{
			int n = (samples.Length - phase) / samplesPerBit;
			if (n <= 0)
				return Array.Empty<bool>();

			var bits = new bool[n];
			for (int i = 0; i < n; i++)
			{
				int start = phase + i * samplesPerBit;
				var window = samples.AsSpan(start, samplesPerBit);
				double mark = Goertzel.Power(window, settings.MarkHz, sampleRate);
				double space = Goertzel.Power(window, settings.SpaceHz, sampleRate);
				bits[i] = mark >= space;
			}
			return bits;
		}

		private static Mdc1200Packet FindPacket(bool[] bits, Mdc1200Settings settings)
		{
			// Search for the 16-bit sync word, then read 48 payload+crc bits.
			for (int start = 0; start + 16 + 48 <= bits.Length; start++)
			{
				if (ReadValue(bits, start, 16) != settings.SyncWord)
					continue;

				int p = start + 16;
				byte op = (byte)ReadValue(bits, p, 8);
				byte arg = (byte)ReadValue(bits, p + 8, 8);
				byte unitHi = (byte)ReadValue(bits, p + 16, 8);
				byte unitLo = (byte)ReadValue(bits, p + 24, 8);
				ushort crc = (ushort)ReadValue(bits, p + 32, 16);

				var payload = new byte[] { op, arg, unitHi, unitLo };
				if (Crc16Ccitt(payload) == crc)
				{
					return new Mdc1200Packet
					{
						Op = op,
						Arg = arg,
						UnitId = (ushort)((unitHi << 8) | unitLo)
					};
				}
			}
			return null;
		}

		private static int ReadValue(bool[] bits, int start, int count)
		{
			int value = 0;
			for (int i = 0; i < count; i++)
				value = (value << 1) | (bits[start + i] ? 1 : 0);
			return value;
		}
	}
}
