// The namespace deliberately names no module: a location written as Elsewhere.Pulse.Tick has to be
// bound by finding the module that declares the type, since no segment of it is an assembly name.
#pragma warning disable IDE0130
namespace Elsewhere;
#pragma warning restore IDE0130

/// <summary>
/// A method in a namespace its assembly is not named for, which is the ordinary shape of a repository
/// that names its assemblies and its namespaces apart. Called once per loop and not inlined, so a
/// breakpoint set on it by name alone has an entry to bind to and a hit to report.
/// </summary>
internal static class Pulse
{
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	internal static void Tick(int iteration)
	{
		_ = iteration;
	}
}
