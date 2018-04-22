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
	}
}