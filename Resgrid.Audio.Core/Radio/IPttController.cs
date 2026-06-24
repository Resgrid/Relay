using System;

namespace Resgrid.Audio.Core.Radio
{
	/// <summary>
	/// Keys/unkeys a physical radio's push-to-talk line. Implementations cover the
	/// common responder interfaces: VOX (no line), serial RTS/DTR, and CM108 GPIO.
	/// </summary>
	public interface IPttController : IDisposable
	{
		bool IsKeyed { get; }
		void Key();
		void Unkey();
	}
}
