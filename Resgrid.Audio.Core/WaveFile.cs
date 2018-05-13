using System;
using System.IO;
using System.Text;

namespace Resgrid.Audio.Core
{
	public class WavFile
	{
		public int _nOriginalLen;
		public int _nHz;
		public int _nBitsPerSample;
		public int _nBytesPerSec;
		public int _nChannels; // mono = 1, stereo = 2

		public enum Domain
		{
			TimeDomain = 1,
			FreqDomain = 2
		};

		Domain currentDomain = Domain.TimeDomain;
		int _np2; // nearest power of 2
		public double[] _aSamples;
		public double[] _aImaginary;

		public void FFT(bool fInverse)
		{
			var n = _aImaginary.Length;
			var nlg2 = (int)(Math.Log(n) / Math.Log(2));

			{
				var j = n / 2;

				if (fInverse)
				{
					for (int i = 0; i < n; i++)
					{
						_aImaginary[i] = -_aImaginary[i];
					}
				}

				for (int i = 1; i < n - 2; i++) // Bit Reversal order
				{
					if (i < j)
					{
						swap(ref _aSamples[j], ref _aSamples[i]);
						swap(ref _aImaginary[j], ref _aImaginary[i]);
					}

					var k = n / 2;

					while (k <= j)
					{
						j -= k;
						k /= 2;
					}

					j += k;
				}
			}

			var le2 = 1;

			for (int lp = 0; lp < nlg2; lp++)
			{
				var le = 2 * le2;
				var ur = 1.0;
				var ui = 0.0;
				var sr = Math.Cos(Math.PI / le2);
				var si = -Math.Sin(Math.PI / le2);
				double tr;
				double ti;

				for (int j = 0; j < le2; j++) // each sub DFT
				{
					for (int i = j; i < n; i += le) // butterfly loop: cross multiply and accumulate
					{
						var ip = i + le2;
						tr = _aSamples[ip] * ur - _aImaginary[ip] * ui;
						ti = _aSamples[ip] * ui + _aImaginary[ip] * ur;
						_aSamples[ip] = _aSamples[i] - tr;
						_aImaginary[ip] = _aImaginary[i] - ti;
						_aSamples[i] = _aSamples[i] + tr;
						_aImaginary[i] = _aImaginary[i] + ti;
					}

					tr = ur;
					ur = tr * sr - ui * si;
					ui = tr * si + ui * sr;
				}

				le2 *= 2;

			}

			if (fInverse)
			{
				for (int i = 0; i < n; i++)
				{
					_aSamples[i] = _aSamples[i] / n;
					_aImaginary[i] = -_aImaginary[i] / n;
				}

				currentDomain = Domain.TimeDomain;
			}
			else
			{
				currentDomain = Domain.FreqDomain;
			}
		}

		public void EnsureDomain(Domain toDomin)

		{

			if (currentDomain != toDomin)

			{

				if (currentDomain == Domain.TimeDomain)

				{

					FFT(fInverse: false);

				}

				else

				{

					FFT(fInverse: true);

				}

			}

		}

		public void NotchFilter(double nFreqNotch, int nNotchWidth)

		{

			EnsureDomain(Domain.FreqDomain);

			// the Sine wave freq is 2048 hz, so we want the filter to be centered on that

			int nMid = (int)(nFreqNotch / _nHz * _np2);



			var nRange = nNotchWidth;

			for (int i = nMid - nRange; i < nMid + nRange; i++)

			{  // we want to set all values in the range to 0

				if (i >= 0 && i < _aSamples.Length)

				{

					_aSamples[i] = 0;

					_aImaginary[i] = 0;

					_aSamples[_np2 - i] = 0;

					_aImaginary[_np2 - i] = 0;

				}

			}



			//for (int i = 0; i < _aImaginary.Length; i++)

			//{

			//    var powerdensity = _aSamples[i] * _aSamples[i] + _aImaginary[i] * _aImaginary[i];

			//    if (powerdensity < 100000000)

			//    {

			//        _aSamples[i] = 0;

			//        _aImaginary[i] = 0;

			//    }

			//}

		}



		public void Reverse()

		{

			EnsureDomain(Domain.TimeDomain);

			for (int i = 0; i < _aSamples.Length / 2; i++)

			{

				swap(ref _aSamples[i], ref _aSamples[_aSamples.Length - i - 1]);

			}

		}



		public void AddSine(double nFreq, double nVolume)

		{

			for (int i = 0; i < _aSamples.Length; i++)

			{ //superposition

				var sum = nVolume * Math.Sin(i * 2 * Math.PI * nFreq / _nHz);      // generate a SIN wave

				var y = _aSamples[i];

				_aSamples[i] += sum;

			}

		}



		public void FreqShift(double nFreqShift)

		{

			//EnsureDomain(Domain.FreqDomain);

			//var n = _aImaginary.Length;

			//var nover4 = _aImaginary.Length / 4;

			//for (int i = 0; i < nover4; i++)

			//{

			//    _aSamples[i] = _aSamples[i + nover4];

			//    _aImaginary[i] = _aImaginary[i + nover4];

			//}

			//for (int i = nover4; i < n / 2; i++)

			//{

			//    _aSamples[i] = 0;

			//    _aImaginary[i] = 0;

			//}



			//for (int i = 0, j = n - 1; i < n / 2; i++, j--)

			//{

			//    _aSamples[j] = _aSamples[i];

			//    _aImaginary[j] = _aImaginary[i];

			//}

			//EnsureDomain(Domain.TimeDomain);

			//for (int i = 0; i < _aImaginary.Length/2; i++)

			//{

			//    _aSamples[i] = _aSamples[2 * i];

			//}

			if (nFreqShift >= 1)

			{

				_nHz *= 2;

			}

			else

			{

				_nHz /= 2;

			}





		}



		public void ReadWav(string fileName)

		{

			currentDomain = Domain.TimeDomain;

			using (var file = System.IO.File.Open(fileName, FileMode.Open, FileAccess.Read))

			{

				if (file.ReadStr(4) != "RIFF")

				{

					throw new InvalidDataException("not wav format");

				}

				var nFileLen = file.ReadInt() + 8;

				if (file.ReadStr(8) != "WAVEfmt ")

				{

					throw new InvalidDataException("not wav format");

				}

				var nSubchunk = file.ReadInt();



				var AudioFormat = file.ReadInt16(); // Audio format 1=Pulse Code Modulated(PCM)

				if (AudioFormat != 1)

				{

					throw new InvalidDataException("Only PCM supported");

				}

				_nChannels = file.ReadInt16();// # of channels 1=mono

				_nHz = file.ReadInt(); // Samples per second

				_nBytesPerSec = file.ReadInt(); // Bytes/sec (Samples/sec * NumChan * BitsPerSample/8)

				var nBlkAlign = file.ReadInt16(); // Block Align = NumChan * BitsPerSample/8

				_nBitsPerSample = file.ReadInt16();// Bits/sample

				var ExtraPadding = file.ReadStr(nSubchunk - 16);

				while (file.Position < file.Length)

				{

					var cSection = file.ReadStr(4);

					switch (cSection)

					{

						case "fact":

							var nFactchunk = file.ReadInt();

							var nRealSize = file.ReadInt(); //uncompressed # of samples

							break;

						case "data":

							var nBytesData = file.ReadInt();

							var nSamples = (int)(nBytesData / (_nBitsPerSample / 8));

							_aSamples = new double[nSamples];

							for (int i = 0; i < nSamples; i++)

							{

								switch (_nBitsPerSample)

								{

									case 8:

										_aSamples[i] = file.ReadByte() - 128;

										break;

									case 16:

										_aSamples[i] = file.ReadInt16();

										break;

								}

							}

							break;

						default:

							throw new InvalidDataException("unknown section");

					}

				}

			}

			_nOriginalLen = _aSamples.Length;

			_np2 = (int)Math.Pow(2, Math.Round((Math.Log(_nOriginalLen) / Math.Log(2)), 0)); // round array len to nearest power of 2

			if (_np2 < _nOriginalLen)

			{

				_np2 *= 2;

				Array.Resize<double>(ref _aSamples, _np2);

				for (int i = _nOriginalLen + 1; i < _np2; i++)

				{

					_aSamples[i] = 0;

				}

			}

			_aImaginary = new double[_np2];

		}

		private void swap<T>(ref T a, ref T b)

		{

			T tmp;

			tmp = a;

			a = b;

			b = tmp;

		}

		public void WriteWav(string fileName)

		{

			EnsureDomain(Domain.TimeDomain);

			using (var file = System.IO.File.Create(fileName))

			{

				// CD quality is 44Khz 16 bits/sample

				var nFileLen = 0; //  total files size in bytes

				var nTrail = 10;

				var nSamples = _aSamples.Length;

				file.Write("RIFF");

				file.Write(0); // placeholder for file size to be written later

				file.Write("WAVE");

				file.Write("fmt ");

				file.Write(16); // Subchunk1 size

				file.Write((Int16)1); // Audio format 1 = Pulse Code Modulated (PCM)

				file.Write((Int16)_nChannels);

				file.Write(_nHz); // # samples per second



				file.Write(_nHz * _nChannels * _nBitsPerSample / 8);       // Bytes/sec (Samples/sec * NumChan * BitsPerSample/8)



				file.Write((Int16)(_nChannels * _nBitsPerSample / 8)); //  && Block Align = NumChan * BitsPerSample/8



				file.Write((Int16)_nBitsPerSample);//       && Bits/sample

				file.Write("data"); //"data" subchunk

				file.Write(_nChannels * (nSamples + nTrail) * nSamples / 8);       //&& # of bytes in data



				for (int x = 0; x < nSamples; x++)

				{

					var y = _aSamples[x];

					switch (_nBitsPerSample)

					{

						case 8: //unsigned, 0 to 255

							y = Math.Max(Math.Min(y, 127), -128);

							file.Write((byte)(y + 128));

							break;

						case 16: //2's comp, -32768 to 32767

							y = Math.Max(Math.Min(y, 32767), -32768);

							file.Write((Int16)(y));

							break;

					}

				}

				for (int i = 0; i < nTrail; i++)

				{

					switch (_nBitsPerSample)

					{

						case 8: //unsigned, 0 to 255

							file.Write((byte)128); //write 0s to bring signal down to 0

							break;

						case 16: //2's comp, -32768 to 32767

							file.Write((Int16)0); //write 0s to bring signal down to 0

							break;

					}

				}

				nFileLen = (nSamples + nTrail) * _nBitsPerSample / 8 + 44;

				file.Seek(4, SeekOrigin.Begin);

				file.Write(nFileLen);

				file.Seek(0, SeekOrigin.End);

			}

		}

	}

	public static class Extensions

	{

		public static int ReadInt(this FileStream file)

		{

			var arr = new byte[4];

			file.Read(arr, 0, 4);

			return BitConverter.ToInt32(arr, 0);

		}

		public static Int16 ReadInt16(this FileStream file)

		{

			return (Int16)(file.ReadByte() + (file.ReadByte() << 8));

		}

		public static string ReadStr(this FileStream file, int nlen)

		{

			var arr = new byte[nlen];

			file.Read(arr, 0, nlen);

			var str = Encoding.ASCII.GetString(arr);

			return str;

		}

		public static void Write(this FileStream file, byte[] arr)

		{

			file.Write(arr, 0, arr.Length);

		}

		public static void Write(this FileStream file, string str)

		{

			file.Write(Encoding.ASCII.GetBytes(str));

		}

		public static void Write(this FileStream file, int num)

		{

			file.Write(BitConverter.GetBytes(num));

		}

		public static void Write(this FileStream file, Int16 num)

		{

			file.Write(BitConverter.GetBytes(num));

		}

	}
}
