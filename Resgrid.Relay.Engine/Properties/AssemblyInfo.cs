using System.Runtime.CompilerServices;

// Exposes engine internals (e.g. RelayServiceBase.ComputeBackoff) to the resilience tests.
[assembly: InternalsVisibleTo("Resgrid.Audio.Voice.Tests")]
