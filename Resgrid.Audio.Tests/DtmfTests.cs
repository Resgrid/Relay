using System.Linq;
using DtmfDetection.NAudio;
using FluentAssertions;
using NAudio.Wave;
using NUnit.Framework;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class DtmfTests
	{
		[Test]
		public void DTMF_ShouldBeAbleToReadWavFile()
		{
			var path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace("file:\\", "");
			using (var waveFile = new WaveFileReader(path + "\\Data\\TestAudio.wav"))
			{
				var actualTones = waveFile.DtmfTones(false).ToArray();

				actualTones.Should().NotBeNullOrEmpty();
			}
		}
	}
}
