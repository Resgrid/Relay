using System;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>
	/// Keys/unkeys a physical radio's push-to-talk line. Implementations cover the
	/// common responder interfaces: VOX (no line), serial RTS/DTR, and CM108 GPIO.
	/// </summary>
	public interface IPttController : IDisposable
	{
		/// <summary>The state the PTT line was last driven to successfully.</summary>
		bool IsKeyed { get; }

		/// <summary>Keys the radio. Returns false when the line could not be driven; <see cref="IsKeyed"/> is then unchanged.</summary>
		bool Key();

		/// <summary>Unkeys the radio. Returns false when the line could not be driven; <see cref="IsKeyed"/> is then unchanged.</summary>
		bool Unkey();
	}
}
