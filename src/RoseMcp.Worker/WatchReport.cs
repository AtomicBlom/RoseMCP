namespace RoseMcp.Worker;

/// <summary>
/// What the watcher noticed since the last barrier: what kind of trouble, and which build files moved.
/// <para>
/// Build files only. The sweep finds every tracked document that changed and the directory walk every
/// source file that appeared, whatever the count, so neither needs the event stream. A build file is
/// different: one appearing is invisible to both, and one changing that the sweep does not track is
/// still a reason to reload when a project could not be evaluated and might import it.
/// </para>
/// </summary>
public sealed record WatchReport
{
	public static readonly WatchReport None = new();

	public WatchSignal Signal { get; init; }

	/// <summary>Build files that appeared or were renamed into place.</summary>
	public IReadOnlyList<string> BuildFilesAppeared { get; init; } = [];

	/// <summary>Build files that changed, were removed, or were renamed away.</summary>
	public IReadOnlyList<string> BuildFilesChanged { get; init; } = [];

	/// <summary>
	/// This report followed by one drained later, read as one. The later report's answers about the
	/// present -- whether git is still writing, whether the solution file is there -- replace this one's,
	/// and what either saw happen is kept, so a drain taken after waiting for git loses nothing the first
	/// drain held.
	/// </summary>
	/// <param name="later">The report drained after this one.</param>
	public WatchReport Then(WatchReport later)
	{
		const WatchSignal Present = WatchSignal.GitOperationInFlight | WatchSignal.SolutionMissing;

		return new WatchReport
		{
			Signal = (Signal & ~Present) | later.Signal,
			BuildFilesAppeared = [.. BuildFilesAppeared.Union(later.BuildFilesAppeared, StringComparer.OrdinalIgnoreCase)],
			BuildFilesChanged = [.. BuildFilesChanged.Union(later.BuildFilesChanged, StringComparer.OrdinalIgnoreCase)],
		};
	}

	public bool HasFlag(WatchSignal flag) => Signal.HasFlag(flag);
}
