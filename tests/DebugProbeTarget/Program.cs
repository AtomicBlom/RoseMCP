namespace DebugProbeTarget;

/// <summary>
/// A tiny modern-.NET target for the live-app attach tests. It throws and catches a distinctively
/// named exception on a short loop, so a debugger that attaches captures a first-chance exception
/// within a moment without any coordination between the two processes. It self-terminates after a
/// bounded lifetime, so an orphan left by a crashed test cannot outlive the run.
/// <para>
/// That lifetime has to be longer than the whole suite, not longer than one test. It was 120 seconds,
/// which was comfortable when a live-app test had the machine to itself and became wrong once they
/// ran together: four tests end by asserting their target is still running, and under a loaded
/// suite the target reached its deadline first and exited, correctly, before they looked. The tests
/// then failed on the fixture having done exactly what it was told. Ten minutes is longer than any
/// run this suite has had and still bounds an orphan to something a person will not notice.
/// </para>
/// <para>
/// Dying when stdin closes -- the way <c>RoseMcp.Worker</c> dies with the broker, which is the right
/// shape for this and was tried first -- is not usable here. Each target passes on its own and the
/// nine debugger tests hang as a group, reproducibly, with no test completing at all. Recorded rather
/// than explained: the interaction between a redirected stdin, inherited handles across nine
/// concurrently launched children and an <c>ICorDebug</c> attach is not understood, and a fixture that
/// can hang the suite is worse than the deadline it was replacing.
/// </para>
/// </summary>
internal static class Program
{
	private static void Main()
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
		var iteration = 0;
		while (DateTime.UtcNow < deadline)
		{
			Beat(iteration++);
			Inspect(new ProbeState { Count = iteration, Inner = new ProbeState { Count = -1, Label = "inner" } });

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
	/// Called once per loop with an object graph a debugger can stop on and evaluate: <c>state.Label</c>
	/// and <c>state.Inner.Count</c> are stable field-access chains the evaluation test reads. Not inlined,
	/// so a breakpoint has a real method to bind to and the argument is live at the stop.
	/// </summary>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void Inspect(ProbeState state)
	{
		_ = state.Count;
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

/// <summary>
/// A small object graph the evaluation test drills into. Public fields (not properties) so a field-access
/// evaluator can read them without running a getter.
/// </summary>
internal sealed class ProbeState
{
	public int Count;

	public string Label = "beat";

	public ProbeState? Inner;
}
