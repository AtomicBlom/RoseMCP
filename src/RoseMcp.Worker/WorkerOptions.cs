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
	/// Stop synthesising stand-in partials for XAML files. The markup compiler only runs in a real
	/// build, so without them a XAML project reports thousands of errors that are not there -- but a
	/// stub is an approximation, and there has to be a way to look at the workspace without one.
	/// </summary>
	public bool NoXamlStubs { get; init; }

	/// <summary>
	/// MSBuild configuration to load under. Null lets <see cref="BuildProperties"/> decide, which
	/// leaves MSBuild's own default alone unless the solution demonstrably does not declare it.
	/// </summary>
	public string? Configuration { get; init; }

	/// <summary>MSBuild platform to load under, on the same terms as <see cref="Configuration"/>.</summary>
	public string? Platform { get; init; }

	/// <summary>
	/// Any other MSBuild global properties to load under. Needed where neither configuration nor
	/// platform is what selects the target framework -- a Revit add-in built as Release for four API
	/// versions distinguishes them by a RevitVersion property and nothing else.
	/// </summary>
	public IReadOnlyDictionary<string, string> Properties { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
		string? configuration = null;
		string? platform = null;
		var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var noRestore = false;
		var noXamlStubs = false;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--solution" or "-s":
					if (i + 1 >= args.Length) throw new ArgumentException("--solution requires a path.");
					solutionPath = args[++i];
					break;

				case "--configuration" or "-c":
					if (i + 1 >= args.Length) throw new ArgumentException("--configuration requires a name.");
					configuration = args[++i];
					break;

				case "--platform":
					if (i + 1 >= args.Length) throw new ArgumentException("--platform requires a name.");
					platform = args[++i];
					break;

				case "--property" or "-p":
					if (i + 1 >= args.Length) throw new ArgumentException("--property requires Name=Value.");
					Add(properties, args[++i]);
					break;

				case "--no-restore":
					noRestore = true;
					break;

				case "--no-xaml-stubs":
					noXamlStubs = true;
					break;

				default:
					throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
			}
		}

		if (string.IsNullOrWhiteSpace(solutionPath)) throw new ArgumentException("--solution is required.");

		return new WorkerOptions
		{
			SolutionPath = Path.GetFullPath(solutionPath),
			Configuration = configuration,
			Platform = platform,
			Properties = properties,
			NoRestore = noRestore,
			NoXamlStubs = noXamlStubs,
		};
	}

	private static void Add(Dictionary<string, string> properties, string assignment)
	{
		var separator = assignment.IndexOf('=', StringComparison.Ordinal);
		if (separator <= 0) throw new ArgumentException($"--property expects Name=Value, got '{assignment}'.");

		properties[assignment[..separator].Trim()] = assignment[(separator + 1)..].Trim();
	}
}
