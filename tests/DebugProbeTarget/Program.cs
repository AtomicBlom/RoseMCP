namespace DebugProbeTarget;

/// <summary>
/// A tiny modern-.NET target for the live-app attach tests. It throws and catches a distinctively
/// named exception on a short loop, so a debugger that attaches captures a first-chance exception
/// within a moment without any coordination between the two processes. It self-terminates after a
/// bounded lifetime, so an orphan left by a crashed test cannot outlive the run.
/// </summary>
internal static class Program
{
	private static void Main()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
		var iteration = 0;
		while (DateTime.UtcNow < deadline)
		{
			Beat(iteration++);

			try
			{
				throw new RoseDebugProbeException();
			}
			catch (RoseDebugProbeException)
			{
				// Thrown only so an attached debugger sees it first-chance; nothing to handle here.
			}

			Thread.Sleep(200);
		}
	}

	/// <summary>
	/// Called once per loop so an attached debugger can bind a tracepoint to it and see it hit. Not
	/// inlined -- a tracepoint binds to a method, and an inlined method has no standalone entry.
	/// </summary>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void Beat(int iteration)
	{
		// The parameter keeps the call from being optimised away and gives the JIT a real body.
		_ = iteration;
	}
}

/// <summary>The distinctively named exception the attach test looks for in the debug event stream.</summary>
internal sealed class RoseDebugProbeException() : Exception("rose debug probe");
