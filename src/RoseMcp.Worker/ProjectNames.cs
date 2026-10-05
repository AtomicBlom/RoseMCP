using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>
/// Turns the project a caller named into the projects it means, the one way every tool does it.
/// <para>
/// A name that matches nothing is refused rather than widened to the solution. The wide answer is
/// the shape of failure this whole surface is built against: it comes back clean and complete, for a
/// question many projects larger than the one asked, and a caller who mistyped a name reads it as an
/// answer about that project. Narrowing to nothing is as bad the other way round -- an empty list of
/// references reads exactly like a symbol nobody uses, which invites a deletion. A refusal naming what
/// the solution does have costs one call, and the caller can copy the right name out of it.
/// </para>
/// <para>
/// One place, because the tools that each did this for themselves disagreed: some widened, some
/// returned nothing, some refused; some accepted a path and some did not. Whichever tool a caller
/// reached first taught it a rule the next tool did not keep.
/// </para>
/// </summary>
public static class ProjectNames
{
	/// <summary>
	/// The projects <paramref name="named"/> means, or every project when it is empty.
	/// </summary>
	/// <exception cref="ArgumentException">A name was given and no project carries it.</exception>
	public static IReadOnlyList<Project> ResolveOrAll(Solution solution, string? named) =>
		string.IsNullOrWhiteSpace(named) ? [.. solution.Projects] : Resolve(solution, named);

	/// <summary>
	/// The projects <paramref name="named"/> means: those of that name, ignoring case, or whose project
	/// file is at that path.
	/// <para>
	/// Several, not one, wherever one project file is loaded more than once. A multi-targeted project
	/// is a Roslyn project per framework, named <c>Library(net8.0)</c> and <c>Library(net10.0)</c>, and
	/// a caller writing <c>Library</c> means the project file rather than one of its compilations --
	/// which is also what the path to it names.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">No project carries the name or lives at the path.</exception>
	public static IReadOnlyList<Project> Resolve(Solution solution, string named)
	{
		var wanted = named.Trim();
		var path = FullPath(wanted);

		var matches = solution.Projects
			.Where(project => string.Equals(project.Name, wanted, StringComparison.OrdinalIgnoreCase)
				|| IsFrameworkOf(project.Name, wanted)
				|| SamePath(project.FilePath, path))
			.ToArray();

		if (matches.Length > 0) return matches;

		var names = solution.Projects
			.Select(project => project.Name)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal);

		throw new ArgumentException(
			$"No project in this solution is called '{wanted}'. It has {string.Join(", ", names)}. Name one of "
				+ "those, or give the path to its project file.");
	}

	/// <summary>
	/// Whether a project's name is the one asked for with the framework a multi-targeted project's
	/// name carries in brackets after it.
	/// </summary>
	private static bool IsFrameworkOf(string name, string wanted) =>
		name.EndsWith(')')
			&& name.Length > wanted.Length + 2
			&& name[wanted.Length] == '('
			&& name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);

	/// <summary>The path the text names, or null where it cannot be one.</summary>
	private static string? FullPath(string text)
	{
		try
		{
			return Path.GetFullPath(text);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

	private static bool SamePath(string? candidate, string? path) =>
		candidate is { Length: > 0 }
			&& path is not null
			&& string.Equals(Path.GetFullPath(candidate), path, StringComparison.OrdinalIgnoreCase);
}
