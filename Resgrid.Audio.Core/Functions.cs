using System;
using System.Linq;
using System.Numerics;

namespace Resgrid.Audio.Core
{
	public static class Functions
	{
		public static double[] FFT(double[] data)
		{
			double[] fft;// = new double[data.Length]; // this is where we will store the output (fft)

			if (data.Length >= 16384)
			{
				var size = Math.Ceiling(data.Length / 16384m) * 16384;
				fft = ArrayLeftPad(data, 0, (int) size);
			}
			else
			{
				fft = ArrayLeftPad(data, 0, 16384);
			}

			Complex[] fftComplex = new Complex[fft.Length]; // the FFT function requires complex format
			for (int i = 0; i < fft.Length; i++)
			{
				fftComplex[i] = new Complex(fft[i], 0.0); // make it complex format (imaginary = 0)
			}

			if (data.Length > 16384)
			{
				int pageNumber = 1;
				var queryResultPage = fftComplex
					.Skip(16384 * pageNumber)
					.Take(16384);

				while (queryResultPage != null && queryResultPage.Count() > 0)
				{
					Accord.Math.FourierTransform.FFT(queryResultPage.ToArray(), Accord.Math.FourierTransform.Direction.Forward);

					pageNumber++;
					queryResultPage = fftComplex
						.Skip(16384 * pageNumber)
						.Take(16384);
				}
			}
			else
			{
				Accord.Math.FourierTransform.FFT(fftComplex, Accord.Math.FourierTransform.Direction.Forward);
			}
			//for (int i = 0; i < data.Length; i++)
			//{
			//	fft[i] = fftComplex[i].Magnitude; // back to double
			//																		//fft[i] = Math.Log10(fft[i]); // convert to dB
			//}
			//return fft;

			return fftComplex.Select(x => x.Magnitude).ToArray();
		}

		public static double[] WaveDataToFFT(double[] data)
		{
			int SAMPLE_RESOLUTION = 16;
			int BYTES_PER_POINT = SAMPLE_RESOLUTION / 8;

			Int32[] vals = new Int32[data.Length / BYTES_PER_POINT];
			double[] Ys = new double[data.Length / BYTES_PER_POINT];

			for (int i = 0; i < vals.Length; i++)
			{
				// bit shift the byte buffer into the right variable format
				byte hByte = (byte)data[i * 2 + 1];
				byte lByte = (byte)data[i * 2 + 0];

				vals[i] = (int)(short)((hByte << 8) | lByte);
				Ys[i] = vals[i];
				//Xs2[i] = ((double)i / Ys.Length * RATE / 1000.0).ToString(); // units are in kHz
			}

			return Functions.FFT(Ys);
		}

		// https://stackoverflow.com/questions/13658006/audio-file-reading-for-fft?utm_medium=organic&utm_source=google_rich_qa&utm_campaign=google_rich_qa
		public static double[] WaveFileDataPrepare(String wavePath, out int SampleRate)
		{
			double[] data;
			byte[] wave;
			byte[] sR = new byte[4];
			System.IO.FileStream WaveFile = System.IO.File.OpenRead(wavePath);
			wave = new byte[WaveFile.Length];
			data = new double[(wave.Length - 44) / 4];//shifting the headers out of the PCM data;
			WaveFile.Read(wave, 0, Convert.ToInt32(WaveFile.Length));//read the wave file into the wave variable
																	 /***********Converting and PCM accounting***************/
			for (int i = 0; i < data.Length - i * 4; i++)
			{
				data[i] = (BitConverter.ToInt32(wave, (1 + i) * 4)) / 65536.0;
			}
			/**************assigning sample rate**********************/
			for (int i = 24; i < 28; i++)
			{
				sR[i - 24] = wave[i];
			}
			SampleRate = BitConverter.ToInt32(sR, 0);
			return data;
		}

		public static double[] ArrayLeftPad(double[] input, double padValue, int len)
		{
			var temp = Enumerable.Repeat(padValue, len).ToArray();
			for (var i = 0; i < input.Length; i++)
				temp[i] = input[i];

			return temp;
		}
	}
}
