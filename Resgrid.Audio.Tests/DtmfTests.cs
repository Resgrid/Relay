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
			var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Data", "TestAudio.wav");
			using (var waveFile = new WaveFileReader(path))
			{
				var actualTones = waveFile.DtmfTones(false).ToArray();

				actualTones.Should().NotBeNullOrEmpty();
			}
		}
	}
}
