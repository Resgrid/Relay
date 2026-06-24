namespace Resgrid.Audio.Voice.Audio
{
	/// <summary>
	/// G.711 µ-law and A-law companding decoders. The Resgrid TTS service returns
	/// 8 kHz mono µ-law WAV; these decode it to linear PCM16 for resampling.
	/// </summary>
	public static class MuLaw
	{
		private const int Bias = 0x84;

		/// <summary>Decodes a single µ-law byte to a 16-bit linear sample.</summary>
		public static short Decode(byte mulaw)
		{
			mulaw = (byte)~mulaw;
			int sign = mulaw & 0x80;
			int exponent = (mulaw >> 4) & 0x07;
			int mantissa = mulaw & 0x0F;
			int sample = ((mantissa << 3) + Bias) << exponent;
			sample -= Bias;
			return (short)(sign != 0 ? -sample : sample);
		}

		/// <summary>Decodes a single A-law byte to a 16-bit linear sample.</summary>
		public static short DecodeALaw(byte alaw)
		{
			alaw ^= 0x55;
			int sign = alaw & 0x80;
			int exponent = (alaw & 0x70) >> 4;
			int mantissa = alaw & 0x0F;
			int sample = (mantissa << 4) + 8;
			if (exponent != 0)
				sample = (sample + 0x100) << (exponent - 1);
			return (short)(sign != 0 ? sample : -sample);
		}
	}
}
