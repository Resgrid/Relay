namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// A single live receive-level sample emitted by the radio tuner: the measured input
	/// level in dBFS and whether the squelch gate is currently open. Surfaced via
	/// <see cref="System.IProgress{T}"/> so a console meter or the WPF Radio tuner can render
	/// it without the engine taking a UI dependency.
	/// </summary>
	public readonly record struct TuneSample(double Dbfs, bool SquelchOpen);
}
