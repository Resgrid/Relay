using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Voice.ToneOut
{
	/// <summary>
	/// Synthesizes spoken audio for a dispatch announcement. Implementations return
	/// PCM16 mono at the engine rate (48 kHz) ready to publish to a PTT channel.
	/// </summary>
	public interface ITextToSpeech
	{
		Task<short[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
	}
}
