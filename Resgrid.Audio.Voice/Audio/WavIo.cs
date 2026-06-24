using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Resgrid.Audio.Voice.Audio
{
	/// <summary>
	/// Minimal, cross-platform WAV (RIFF/PCM) reader and writer for PCM16 and µ-law.
	/// Hand-rolled to avoid any OS-specific media APIs (MediaFoundation/ACM) so the
	/// recorder and tone-out work identically on Windows and Linux.
	/// </summary>
	public static class WavIo
	{
		private const ushort FormatPcm = 1;
		private const ushort FormatMuLaw = 7;
		private const ushort FormatALaw = 6;

		/// <summary>Serializes PCM16 samples to a canonical 44-byte-header WAV.</summary>
		public static byte[] WritePcm16(IReadOnlyList<short> samples, int sampleRate, int channels)
		{
			int dataBytes = samples.Count * 2;
			using var ms = new MemoryStream(44 + dataBytes);
			using var w = new BinaryWriter(ms);

			int byteRate = sampleRate * channels * 2;
			short blockAlign = (short)(channels * 2);

			w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
			w.Write(36 + dataBytes);
			w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
			w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
			w.Write(16);                       // PCM fmt chunk size
			w.Write(FormatPcm);                // audio format = PCM
			w.Write((short)channels);
			w.Write(sampleRate);
			w.Write(byteRate);
			w.Write(blockAlign);
			w.Write((short)16);                // bits per sample
			w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
			w.Write(dataBytes);
			for (int i = 0; i < samples.Count; i++)
				w.Write(samples[i]);

			w.Flush();
			return ms.ToArray();
		}

		/// <summary>
		/// Reads a WAV file into PCM16 mono samples, decoding µ-law/A-law and
		/// downmixing to mono if needed. Returns the decoded samples and source rate.
		/// </summary>
		public static (short[] Samples, int SampleRate) ReadToPcm16Mono(byte[] wav)
		{
			if (wav == null || wav.Length < 12)
				throw new ArgumentException("Not a valid WAV payload.", nameof(wav));

			var span = wav.AsSpan();
			if (span[0] != 'R' || span[1] != 'I' || span[2] != 'F' || span[3] != 'F')
				throw new ArgumentException("Missing RIFF header.", nameof(wav));

			ushort format = 0, channels = 1, bits = 16;
			int sampleRate = 8000;
			int pos = 12;
			ReadOnlySpan<byte> data = default;

			while (pos + 8 <= wav.Length)
			{
				var id = span.Slice(pos, 4);
				int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
				int body = pos + 8;
				if (body + size > wav.Length)
					size = wav.Length - body;

				if (id[0] == 'f' && id[1] == 'm' && id[2] == 't' && id[3] == ' ')
				{
					format = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body, 2));
					channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 2, 2));
					sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(body + 4, 4));
					bits = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 14, 2));
				}
				else if (id[0] == 'd' && id[1] == 'a' && id[2] == 't' && id[3] == 'a')
				{
					data = span.Slice(body, size);
				}

				pos = body + size + (size & 1); // chunks are word-aligned
			}

			if (data.IsEmpty)
				throw new ArgumentException("WAV has no data chunk.", nameof(wav));

			short[] mono = DecodeToMono(format, channels, bits, data);
			return (mono, sampleRate);
		}

		private static short[] DecodeToMono(ushort format, int channels, int bits, ReadOnlySpan<byte> data)
		{
			short[] interleaved;
			if (format == FormatPcm && bits == 16)
			{
				interleaved = new short[data.Length / 2];
				for (int i = 0; i < interleaved.Length; i++)
					interleaved[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
			}
			else if (format == FormatMuLaw)
			{
				interleaved = new short[data.Length];
				for (int i = 0; i < data.Length; i++)
					interleaved[i] = MuLaw.Decode(data[i]);
			}
			else if (format == FormatALaw)
			{
				interleaved = new short[data.Length];
				for (int i = 0; i < data.Length; i++)
					interleaved[i] = MuLaw.DecodeALaw(data[i]);
			}
			else if (format == FormatPcm && bits == 8)
			{
				interleaved = new short[data.Length];
				for (int i = 0; i < data.Length; i++)
					interleaved[i] = (short)((data[i] - 128) << 8);
			}
			else
			{
				throw new NotSupportedException($"Unsupported WAV format {format} / {bits}-bit.");
			}

			if (channels <= 1)
				return interleaved;

			// Downmix to mono by averaging channels.
			int frames = interleaved.Length / channels;
			var mono = new short[frames];
			for (int f = 0; f < frames; f++)
			{
				int sum = 0;
				for (int c = 0; c < channels; c++)
					sum += interleaved[f * channels + c];
				mono[f] = (short)(sum / channels);
			}
			return mono;
		}
	}
}
