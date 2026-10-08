using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// What narrows a reference search: one condition per facet a reference carries, each named after the
/// field it selects on.
/// <para>
/// Every facet a result returns is a filter the tool owes. A fact worth computing on every reference
/// is a fact a caller wants to select on, and one it can read and cannot ask about leaves only two
/// ways to narrow a large answer -- reading all of it, or a text search over it, which throws away the
/// precision the semantic search was paid for.
/// </para>
/// </summary>
public sealed record ReferenceFilter
{
	/// <summary>
	/// The projects <c>project</c> resolved to, by their Roslyn names, which is more than one for a
	/// multi-targeted project; null for every project.
	/// </summary>
	public IReadOnlySet<string>? Projects { get; init; }

	/// <summary><c>project</c> as the caller wrote it, for saying what the filter was.</summary>
	public string? Project { get; init; }

	/// <summary>A member as a reference names it, <c>Type.Member</c>, or a bare member name for every type's.</summary>
	public string? ContainingMember { get; init; }

	/// <summary>True for only references in test projects, false for only those outside them.</summary>
	public bool? IsTestProject { get; init; }

	/// <summary>True for only references in source-generated code, false for only those in files.</summary>
	public bool? IsGenerated { get; init; }

	/// <summary>
	/// True where <see cref="ContainingMember"/> is a key some reference carries whole, which is set by
	/// <see cref="Over"/> and makes it match only that key. False makes it a bare member name, matched in
	/// every type.
	/// </summary>
	public bool MemberIsKey { get; init; }

	/// <summary>
	/// The filter as it applies to these references: <see cref="ContainingMember"/> read as a whole key
	/// where any of them carries it, and as a bare member name otherwise.
	/// <para>
	/// Decided over the whole set rather than per reference, because a shape's groups are keys and a
	/// group passed back has to list exactly what it counted. Read per reference, <c>Widget</c> -- the
	/// key of a reference in a type's base list -- would also match the constructor <c>Widget.Widget</c>.
	/// </para>
	/// </summary>
	public ReferenceFilter Over(IEnumerable<SourceLocation> references) =>
		this with
		{
			MemberIsKey = ContainingMember is { } wanted
				&& references.Any(location => string.Equals(location.ContainingMember, wanted, StringComparison.Ordinal)),
		};

	/// <summary>True where the filter keeps every reference.</summary>
	public bool KeepsAll =>
		Projects is null && ContainingMember is null && IsTestProject is null && IsGenerated is null;

	/// <summary>Whether a reference satisfies every condition the caller gave.</summary>
	public bool Keeps(SourceLocation location)
	{
		var inProject = Projects is null || (location.Project is { } owner && Projects.Contains(owner));
		var inMember = ContainingMember is null || SitsInside(location.ContainingMember, ContainingMember);
		var testMatches = IsTestProject is not { } test || location.IsTestProject == test;
		var generatedMatches = IsGenerated is not { } generated || (location.GeneratedHintName is not null) == generated;

		return inProject && inMember && testMatches && generatedMatches;
	}

	/// <summary>
	/// The conditions as a phrase completing "none of the references is ...", so a filter that kept
	/// nothing says which question had no answer.
	/// </summary>
	public string Describe()
	{
		var conditions = new List<string>();

		if (Projects is not null) conditions.Add($"in project {Project}");
		if (ContainingMember is not null) conditions.Add($"inside a member called {ContainingMember}");
		if (IsTestProject is { } test) conditions.Add(test ? "in a test project" : "outside test projects");
		if (IsGenerated is { } generated) conditions.Add(generated ? "in generated code" : "in a written file");

		return string.Join(" and ", conditions);
	}

	/// <summary>
	/// Whether a reference's containing member is the one asked for. Ordinal, because <c>name</c> and
	/// <c>Name</c> are two members of one type and a group of either has to list only its own.
	/// </summary>
	private bool SitsInside(string? member, string wanted)
	{
		if (member is null) return false;

		return MemberIsKey
			? string.Equals(member, wanted, StringComparison.Ordinal)
			: member.EndsWith($".{wanted}", StringComparison.Ordinal);
	}
}

/// <summary>How references are grouped: by file to list them, and by facet to describe them.</summary>
public static class ReferenceShapes
{
	/// <summary>
	/// How many members a shape names. The members are the grouping most worth reading -- "used by
	/// these six methods" -- and also the only facet with no natural bound, since a hot symbol can sit
	/// in hundreds of them; the count beside the list says how many there are in all.
	/// </summary>
	public const int NamedMembers = 10;

	/// <summary>
	/// The references by the file they are in, in the order given, so what every reference in a file
	/// shares is said once rather than per reference. A file compiled by two projects is two groups,
	/// because the project is part of what the group says.
	/// </summary>
	public static IReadOnlyList<ReferenceFile> ByFile(IEnumerable<SourceLocation> references, bool includePreviews) =>
		[
			.. references
				.GroupBy(location => (location.FilePath, location.Project))
				.Select(file => new ReferenceFile
				{
					FilePath = file.Key.FilePath,
					Project = file.Key.Project,
					IsTestProject = file.First().IsTestProject,
					GeneratedHintName = file.First().GeneratedHintName,
					References =
					[
						.. file.Select(location => new ReferenceSite
						{
							Line = location.Line,
							Column = location.Column,
							ContainingMember = location.ContainingMember,
							Preview = includePreviews ? location.Preview : null,
						}),
					],
				}),
		];

	/// <summary>
	/// How a set of references divides along each facet, with every group keyed by the value its
	/// narrowing argument takes. Ties are broken by name so the same search describes itself the same
	/// way twice.
	/// </summary>
	public static ReferenceShape Of(IReadOnlyCollection<SourceLocation> references)
	{
		var projects = references
			.Where(location => location.Project is not null)
			.GroupBy(location => location.Project!, StringComparer.Ordinal)
			.Select(project => new ProjectReferenceCount
			{
				Project = project.Key,
				Count = project.Count(),
				IsTestProject = project.First().IsTestProject,
			})
			.OrderByDescending(project => project.Count)
			.ThenBy(project => project.Project, StringComparer.Ordinal)
			.ToArray();

		var members = references
			.Where(location => location.ContainingMember is not null)
			.GroupBy(location => location.ContainingMember!, StringComparer.Ordinal)
			.Select(member => new MemberReferenceCount { ContainingMember = member.Key, Count = member.Count() })
			.OrderByDescending(member => member.Count)
			.ThenBy(member => member.ContainingMember, StringComparer.Ordinal)
			.ToArray();

		return new ReferenceShape
		{
			Total = references.Count,
			InTestProjects = references.Count(location => location.IsTestProject),
			InGeneratedCode = references.Count(location => location.GeneratedHintName is not null),
			Projects = projects,
			Members = members.Length > NamedMembers ? members[..NamedMembers] : members,
			MemberCount = members.Length,
		};
	}

	/// <summary>
	/// What an overflow says beside its shape: that the list was withheld, and the questions that would
	/// list fewer, named by the arguments that ask them.
	/// </summary>
	public static string Overflow(int total, int maxResults) =>
		$"{total} references is more than maxResults ({maxResults}), so their shape is given instead of "
			+ "the list. Narrow with project, containingMember, isTestProject or isGenerated -- every "
			+ $"group in the shape is a value one of them takes -- or pass maxResults={total} to list them all.";

	/// <summary>What a search whose filters kept nothing says beside the unfiltered shape.</summary>
	public static string NothingKept(int total, ReferenceFilter filter) =>
		$"None of the {total} references is {filter.Describe()}. The shape given is of all {total}, "
			+ "unfiltered, so the question that has an answer can be read from it.";
}
