namespace RoseMcp.Broker;

/// <summary>
/// Turns whatever path a caller supplies into the solution a worker should own.
/// <para>
/// Agents refer to code by the file they happen to be looking at, not by solution path, so
/// accepting only a .sln would push the search onto every caller. Walking up from any path inside
/// the tree is what makes the <c>workspace</c> argument optional almost everywhere.
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
	public static string Resolve(string path)
	{
		var full = Path.GetFullPath(path);
		var extension = Path.GetExtension(full);

		if (SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return full;

		var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
		if (string.IsNullOrEmpty(directory))
		{
			throw new ArgumentException($"Could not work out a directory from '{path}'.");
		}

		var current = new DirectoryInfo(directory);
		while (current is not null)
		{
			var solutions = SolutionExtensions
				.SelectMany(candidate => SafeGetFiles(current, "*" + candidate))
				.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (solutions.Length > 0) return solutions[0].FullName;

			current = current.Parent;
		}

		// No enclosing solution. A bare project is still perfectly loadable.
		if (ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return full;

		var project = FindNearestProject(directory);
		if (project is not null) return project;

		throw new ArgumentException(
			$"No solution or project found at or above '{path}'. Pass the path to a .sln, .slnx, or .csproj.");
	}

	/// <summary>
	/// Enumerates a directory that may not exist. Callers routinely pass a path under a folder
	/// that has been deleted or never existed, and that should produce the helpful message at the
	/// end of the walk rather than a DirectoryNotFoundException from the middle of it.
	/// </summary>
	private static FileInfo[] SafeGetFiles(DirectoryInfo directory, string pattern)
	{
		try
		{
			return directory.Exists ? directory.GetFiles(pattern) : [];
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
			var projects = ProjectExtensions
				.SelectMany(extension => SafeGetFiles(current, "*" + extension))
				.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (projects.Length > 0) return projects[0].FullName;

			current = current.Parent;
		}

		return null;
	}
}
