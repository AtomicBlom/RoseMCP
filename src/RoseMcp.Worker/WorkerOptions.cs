namespace RoseMcp.Worker;

/// <summary>Command line for a worker process. One worker serves exactly one solution.</summary>
public sealed class WorkerOptions
{
	/// <summary>Absolute path to the .sln, .slnx, or .csproj this worker owns.</summary>
	public required string SolutionPath { get; init; }

	/// <summary>
	/// Skip the automatic <c>dotnet restore</c> that runs when a project has no restore output.
	/// Without restore the design-time build cannot resolve analyzers, so generators produce
	/// nothing -- the worker reports that rather than pretending the load succeeded.
	/// </summary>
	public bool NoRestore { get; init; }

	/// <summary>
	/// How long the solution file may be missing before the worker unloads. Absence is routine:
	/// editors save atomically by delete-then-rename, and checking out a branch without the file
	/// removes and restores it in one operation.
	/// </summary>
	public TimeSpan UnloadGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>How long to wait for a git operation to release its lock before giving up on it.</summary>
	public TimeSpan GitSettleTimeout { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>Poll interval while waiting for git to settle.</summary>
	public TimeSpan GitSettleInterval { get; init; } = TimeSpan.FromMilliseconds(100);

	public static WorkerOptions Parse(string[] args)
	{
		string? solutionPath = null;
		var noRestore = false;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--solution" or "-s":
					if (i + 1 >= args.Length) throw new ArgumentException("--solution requires a path.");
					solutionPath = args[++i];
					break;

				case "--no-restore":
					noRestore = true;
					break;

				default:
					throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
			}
		}

		if (string.IsNullOrWhiteSpace(solutionPath)) throw new ArgumentException("--solution is required.");

		return new WorkerOptions
		{
			SolutionPath = Path.GetFullPath(solutionPath),
			NoRestore = noRestore,
		};
	}
}
