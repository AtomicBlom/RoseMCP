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
		while (DateTime.UtcNow < deadline)
		{
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
}

/// <summary>The distinctively named exception the attach test looks for in the debug event stream.</summary>
internal sealed class RoseDebugProbeException() : Exception("rose debug probe");
