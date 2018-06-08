using System;
using System.Linq;

namespace Resgrid.Audio.Core
{
	public static class AudioFunctions
	{
		public static WaveformEventArgs PrepareAudioData(byte[] buffer, int bytesRecorded)
		{
			int SAMPLE_RESOLUTION = 16;
			int BYTES_PER_POINT = SAMPLE_RESOLUTION / 8;

			Int32[] vals = new Int32[buffer.Length / BYTES_PER_POINT];
			double[] Ys = new double[buffer.Length / BYTES_PER_POINT];
			//string[] Xs = new string[buffer.Length / BYTES_PER_POINT];

			double[] Ys2 = new double[buffer.Length / BYTES_PER_POINT];
			//string[] Xs2 = new string[buffer.Length / BYTES_PER_POINT];

			for (int i = 0; i < vals.Length; i++)
			{
				// bit shift the byte buffer into the right variable format
				byte hByte = buffer[i * 2 + 1];
				byte lByte = buffer[i * 2 + 0];
				vals[i] = (int)(short)((hByte << 8) | lByte);
				//Xs[i] = i.ToString();
				Ys[i] = vals[i];
				//Xs2[i] = ((double)i / Ys.Length * RATE / 1000.0).ToString(); // units are in kHz
			}

			Ys2 = Functions.FFT(Ys);

			return new WaveformEventArgs(Ys, Ys2.Take(Ys2.Length / 2).ToArray());
		}
	}
}
