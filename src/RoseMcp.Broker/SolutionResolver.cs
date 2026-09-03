using RoseMcp.Solutions;

namespace RoseMcp.Broker;

/// <summary>
/// Turns whatever path a caller supplies into the solution a worker should own.
/// <para>
/// Agents refer to code by the file they happen to be looking at, not by solution path, so
/// accepting only a .sln would push the search onto every caller. Walking up from any path inside
/// the tree is what makes the <c>workspace</c> argument optional almost everywhere.
/// </para>
/// <para>
/// A directory holding more than one solution is the case worth care. Picking the first by name is
/// silent and wrong: <c>D:\Drawboard\Revit</c> holds a 17-project solution beside a 1-project
/// installer, and the installer sorts first, so every bare call in that repository answered from
/// the wrong compilation while looking exactly like a true negative. Containment decides it where
/// it can, a committed pin decides what containment leaves tied, and anything still undecided is an
/// error naming the candidates rather than a guess.
/// </para>
/// </summary>
public static class SolutionResolver
{
	private static readonly string[] SolutionExtensions = [".sln", ".slnx", ".slnf"];
	private static readonly string[] ProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

	/// <summary>
	/// The nearest solution at or above <paramref name="path"/>, which may be a solution, a project,
	/// a source file, or a directory. Falls back to a bare project when no solution encloses it.
	/// </summary>
	public static string Resolve(string path) => Choose(path).SolutionPath;

	/// <summary>
	/// As <see cref="Resolve"/>, but says what else was in the running and why this one won, so the
	/// caller can log a choice that later turns out to have been the wrong one.
	/// </summary>
	public static SolutionChoice Choose(string path)
	{
		var full = Path.GetFullPath(path);
		var extension = Path.GetExtension(full);

		if (SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
		{
			return new SolutionChoice
			{
				SolutionPath = full,
				Candidates = [full],
				Reason = "the caller named it",
			};
		}

		var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
		if (string.IsNullOrEmpty(directory))
		{
			throw new ArgumentException($"Could not work out a directory from '{path}'.");
		}

		var current = new DirectoryInfo(directory);
		while (current is not null)
		{
			var solutions = SolutionsIn(current);

			if (solutions.Length == 1)
			{
				return new SolutionChoice
				{
					SolutionPath = solutions[0],
					Candidates = solutions,
					Reason = $"the only solution in {current.FullName}",
				};
			}

			if (solutions.Length > 1) return Disambiguate(solutions, full, current.FullName);

			current = current.Parent;
		}

		// No enclosing solution. A bare project is still perfectly loadable.
		if (ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
		{
			return new SolutionChoice { SolutionPath = full, Candidates = [full], Reason = "the caller named it" };
		}

		var project = FindNearestProject(directory);
		if (project is not null)
		{
			return new SolutionChoice
			{
				SolutionPath = project,
				Candidates = [project],
				Reason = "no solution encloses it, so the nearest project stands in",
			};
		}

		throw new ArgumentException(
			$"No solution or project found at or above '{path}'. Pass the path to a .sln, .slnx, or .csproj.");
	}

	/// <summary>
	/// Other solutions beside <paramref name="solutionPath"/> that also compile some of
	/// <paramref name="files"/>.
	/// <para>
	/// A change is only ever computed against one solution, and Roslyn renames within one
	/// <c>Solution</c>. Where a repository holds several over a shared set of projects, a rename of
	/// shared code rewrites the declaration and every reference the loaded solution can see, then
	/// writes to disk -- where the other solutions pick the new text up immediately and keep calling
	/// the old name from projects this one never had. The result is not a stale sibling but a broken
	/// one, so the least this can do is say which.
	/// </para>
	/// <para>
	/// Only the solution's own directory is searched. That is where a sibling sharing projects
	/// actually lives, and walking up would drag in every unrelated solution in the checkout.
	/// </para>
	/// </summary>
	public static IReadOnlyList<SolutionOverlap> SiblingsSharing(string solutionPath, IReadOnlyList<string> files)
	{
		if (files.Count == 0) return [];

		var full = Path.GetFullPath(solutionPath);
		var directory = Path.GetDirectoryName(full);
		if (string.IsNullOrEmpty(directory)) return [];

		var wanted = files.Select(Path.GetFullPath).ToArray();
		var overlaps = new List<SolutionOverlap>();

		foreach (var sibling in SolutionsIn(new DirectoryInfo(directory)))
		{
			if (sibling.Equals(full, StringComparison.OrdinalIgnoreCase)) continue;

			var shared = wanted.Count(file => Contains(sibling, file));
			if (shared > 0) overlaps.Add(new SolutionOverlap { SolutionPath = sibling, SharedFileCount = shared });
		}

		return overlaps;
	}

	/// <summary>
	/// Chooses between the solutions sharing one directory.
	/// <para>
	/// Containment narrows, then a pin breaks whatever tie is left. Containment goes first because it
	/// is the only criterion actually about the question asked -- the solution that compiles the file
	/// you named is the one that can answer about it -- whereas a pin is an ambient default for the
	/// directory, and evidence about this question beats a default set for all of them.
	/// </para>
	/// <para>
	/// The order used to be the other way round, against this method's own description and against
	/// the name of the test covering it, which only ever resolved a bare directory and so passed
	/// either way. It is not academic. In <c>D:\Drawboard\Windows\Windows.IntegrationFramework</c>
	/// three solutions share the root and the largest is not a superset: 23 projects under
	/// <c>Pdf/</c> and 5 under <c>Shared/</c> are outside it. Pinning it -- which is what the
	/// ambiguity error tells you to do -- made every question about those 28 projects resolve to a
	/// compilation that does not contain the file, which is the wrong-compilation-shaped-like-a-true-
	/// negative failure this class exists to prevent.
	/// </para>
	/// <para>
	/// So a pin still decides a bare directory, which is the case it was added for, and still decides
	/// between several solutions that all compile the path. It just no longer overrules the one that
	/// does when the alternatives do not.
	/// </para>
	/// </summary>
	private static SolutionChoice Disambiguate(string[] candidates, string requested, string directory)
	{
		var containing = candidates.Where(candidate => Contains(candidate, requested)).ToArray();
		if (containing.Length == 1)
		{
			return new SolutionChoice
			{
				SolutionPath = containing[0],
				Candidates = candidates,
				Reason = "the only one of them that compiles that path",
			};
		}

		// Nothing singled the path out, so the directory's own default decides. Among those that do
		// compile it where there were several, among all of them where there were none.
		var pinned = Pinned(containing.Length > 1 ? containing : candidates, directory);
		if (pinned is not null)
		{
			return new SolutionChoice
			{
				SolutionPath = pinned.Value.Path,
				Candidates = candidates,
				Reason = containing.Length > 1
					? $"pinned by {pinned.Value.By}, of the {containing.Length} that compile that path"
					: $"pinned by {pinned.Value.By}",
			};
		}

		throw new AmbiguousSolutionException(directory, candidates, containing);
	}

	/// <summary>
	/// The solution named by a <c>rosemcp.json</c> beside them, when it names one of these.
	/// <para>
	/// A pin naming something absent is ignored rather than fatal, on the same terms as the rest of
	/// that file: a stale name in a committed file should not stop every call in the repository, and
	/// the ambiguity error that follows lists what is actually there.
	/// </para>
	/// </summary>
	private static (string Path, string By)? Pinned(string[] candidates, string directory)
	{
		var config = WorkspaceConfigFile.FindInDirectory(directory);
		if (config?.Solution is not { Length: > 0 } wanted) return null;

		var full = Path.GetFullPath(Path.Combine(directory, wanted));
		var match = candidates.FirstOrDefault(
			candidate => candidate.Equals(full, StringComparison.OrdinalIgnoreCase));

		return match is null ? null : (match, config.Path);
	}

	/// <summary>
	/// Whether <paramref name="solutionPath"/> holds a project whose directory encloses
	/// <paramref name="requested"/>. Reading the project list is a parse, not a build, so this costs
	/// a file read per candidate and never an MSBuild evaluation.
	/// </summary>
	private static bool Contains(string solutionPath, string requested)
	{
		try
		{
			return SolutionFileReader.ReadProjectPaths(solutionPath)
				.Select(Path.GetDirectoryName)
				.Any(projectDirectory => IsUnder(requested, projectDirectory));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool IsUnder(string path, string? directory)
	{
		if (string.IsNullOrEmpty(directory)) return false;

		var root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;

		return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Every solution file directly in a directory, ordered by name so the candidate list a caller
	/// is shown does not shuffle between calls.
	/// <para>
	/// Enumerated once and filtered by extension rather than asked for one glob per extension:
	/// on Windows a three-character search pattern also matches longer extensions, so
	/// <c>*.sln</c> can return the <c>.slnx</c> files as well and the same file lands in the list
	/// twice. Two entries for one solution would read as ambiguity where there is none.
	/// </para>
	/// </summary>
	private static string[] SolutionsIn(DirectoryInfo directory)
	{
		try
		{
			if (!directory.Exists) return [];

			return [.. directory
				.GetFiles()
				.Where(file => SolutionExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
				.Select(file => file.FullName)
				.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static string? FindNearestProject(string directory)
	{
		var current = new DirectoryInfo(directory);
		while (current is not null)
		{
			var projects = SafeGetProjects(current);

			if (projects.Length > 0) return projects[0];

			current = current.Parent;
		}

		return null;
	}

	private static string[] SafeGetProjects(DirectoryInfo directory)
	{
		try
		{
			if (!directory.Exists) return [];

			return [.. directory
				.GetFiles()
				.Where(file => ProjectExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
				.Select(file => file.FullName)
				.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
