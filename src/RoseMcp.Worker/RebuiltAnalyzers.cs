using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// Notices an analyzer, generator or code-fix assembly rebuilt on disk after this process loaded it.
/// <para>
/// An assembly cannot be unloaded, so once one is loaded every answer it takes part in comes from that
/// build: diagnostics, generated code, fixes. Rebuilding it changes nothing a worker can see, and the
/// answers go on describing the old build with nothing to say so. The only cure is a new process, which
/// <c>rose_workspace_reload</c> starts, so this says that rather than reloading the solution in place:
/// an in-place reload takes the same copies from the same loader and changes nothing either.
/// </para>
/// <para>
/// Checked by the read barrier, one stat per distinct analyzer path beside the one per document it
/// already makes. Compared with the stamp the shadow-copying loader recorded when it copied the file,
/// which is the build actually loaded, and which nothing restamps: once rebuilt, an assembly reads as
/// rebuilt until the process goes, unless the file on disk returns to the build that was loaded.
/// </para>
/// </summary>
/// <param name="copiedStamp">The stamp a path had when it was copied for loading, or null where it was not.</param>
/// <param name="ownDirectory">
/// Where this worker's own assemblies are, whose analyzers -- the XAML stub generator -- are left out:
/// they ship with the worker and change only with a new one, so nothing here can be stale about them.
/// </param>
public sealed class RebuiltAnalyzers(Func<string, FileStamp?> copiedStamp, string ownDirectory)
{
	private string[] _paths = [];
	private volatile IReadOnlyList<string> _rebuilt = [];

	/// <summary>
	/// The assemblies the last check found rebuilt, by full path. Safe to read from any thread, which is
	/// how the worker's own info reports them without waiting behind the writer.
	/// </summary>
	public IReadOnlyList<string> Current => _rebuilt;

	/// <summary>
	/// Takes the analyzer references of a freshly loaded solution as the ones to check. Called at load and at
	/// every reload in place, since a reload can drop a reference or bring a new one; a path kept across one
	/// keeps being compared with what was first loaded from it, because that is still what is loaded.
	/// </summary>
	public void Track(Solution solution)
	{
		var own = Path.GetFullPath(ownDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			+ Path.DirectorySeparatorChar;

		Track(solution.Projects
			.SelectMany(project => project.AnalyzerReferences)
			.Select(reference => reference.FullPath)
			.OfType<string>()
			.Where(path => path.Length > 0)
			.Select(Path.GetFullPath)
			.Where(path => !path.StartsWith(own, StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>Takes these paths as the analyzer assemblies to check, once each however many projects share one.</summary>
	public void Track(IEnumerable<string> paths)
	{
		_paths = [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];

		var tracked = _paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
		_rebuilt = [.. _rebuilt.Where(tracked.Contains)];
	}

	/// <summary>
	/// Compares each tracked assembly on disk with the build loaded from it, and returns the ones that differ.
	/// <para>
	/// One not loaded yet is skipped, since whatever is loaded from it later is a copy of what is on disk then.
	/// One missing from disk keeps the verdict it had: a rebuild can delete the file before writing the new
	/// one, and neither calling that rebuilt nor forgetting a rebuild already seen would be true.
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Check()
	{
		var previous = _rebuilt.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var rebuilt = new List<string>();

		foreach (var path in _paths)
		{
			if (copiedStamp(path) is not { } loaded) continue;

			var rebuiltNow = FileStamp.For(path) is { } onDisk ? onDisk != loaded : previous.Contains(path);
			if (rebuiltNow) rebuilt.Add(path);
		}

		_rebuilt = rebuilt;

		return rebuilt;
	}

	/// <summary>
	/// What every read says while <paramref name="rebuilt"/> is not empty, or null where it is: which
	/// assemblies, that answers come from the build loaded, and what picks up the new one.
	/// </summary>
	public static string? Notice(IReadOnlyList<string> rebuilt)
	{
		var names = rebuilt.Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

		var named = names.Length switch
		{
			0 => null,
			1 => $"{names[0]} was",
			2 => $"{names[0]} and {names[1]} were",
			3 => $"{names[0]}, {names[1]} and {names[2]} were",
			_ => $"{names[0]}, {names[1]} and {names.Length - 2} more were",
		};

		if (named is null) return null;

		return $"{named} rebuilt after this worker loaded {(names.Length == 1 ? "it" : "them")}, so the analyzers, generators "
			+ "and code fixes in them are still the build that was loaded: diagnostics, generated code and fixes from them "
			+ "may not match what is on disk. An assembly cannot be unloaded, so rose_workspace_reload, which starts a new "
			+ "worker, is what picks up the new build.";
	}
}
