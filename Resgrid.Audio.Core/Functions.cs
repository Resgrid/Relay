using System;
using System.Linq;
using System.Numerics;

namespace Resgrid.Audio.Core
{
	public static class Functions
	{
		public static double[] FFT(double[] data)
		{
			//double[] fft = new double[data.Length]; // this is where we will store the output (fft)
			Complex[] fftComplex = new Complex[data.Length]; // the FFT function requires complex format
			for (int i = 0; i < data.Length; i++)
			{
				fftComplex[i] = new Complex(data[i], 0.0); // make it complex format (imaginary = 0)
			}

			Accord.Math.FourierTransform.FFT(fftComplex, Accord.Math.FourierTransform.Direction.Forward);
			//for (int i = 0; i < data.Length; i++)
			//{
			//	fft[i] = fftComplex[i].Magnitude; // back to double
			//																		//fft[i] = Math.Log10(fft[i]); // convert to dB
			//}
			//return fft;

			return fftComplex.Select(x => x.Magnitude).ToArray();
		}

		public static double[] WaveDataToFFT(byte[] data)
		{
			int SAMPLE_RESOLUTION = 16;
			int BYTES_PER_POINT = SAMPLE_RESOLUTION / 8;

			Int32[] vals = new Int32[data.Length / BYTES_PER_POINT];
			double[] Ys = new double[data.Length / BYTES_PER_POINT];

			for (int i = 0; i < vals.Length; i++)
			{
				// bit shift the byte buffer into the right variable format
				byte hByte = data[i * 2 + 1];
				byte lByte = data[i * 2 + 0];

				vals[i] = (int)(short)((hByte << 8) | lByte);
				Ys[i] = vals[i];
				//Xs2[i] = ((double)i / Ys.Length * RATE / 1000.0).ToString(); // units are in kHz
			}

			return Functions.FFT(Ys);
		}
	}
}
