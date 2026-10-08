namespace RoseMcp.Contracts;

/// <summary>
/// How a set of diagnostics too long to list divides along each facet a diagnostic carries. Every group
/// is keyed by the value the narrowing argument of the same name takes, so a group read here is a
/// question the next call can ask.
/// </summary>
public sealed record DiagnosticShape
{
	/// <summary>How many diagnostics this describes.</summary>
	public required int Total { get; init; }

	/// <summary>How many of them are in source-generated code, which <c>isGenerated</c> selects.</summary>
	public required int InGeneratedCode { get; init; }

	/// <summary>Every diagnostic id found, most first.</summary>
	public required IReadOnlyList<DiagnosticIdCount> Ids { get; init; }

	/// <summary>Every project with a diagnostic, most first.</summary>
	public required IReadOnlyList<DiagnosticProjectCount> Projects { get; init; }

	/// <summary>
	/// The written files holding the most diagnostics, most first, up to a cap: <see cref="FileCount"/>
	/// says how many there are in all.
	/// </summary>
	public required IReadOnlyList<DiagnosticFileCount> Files { get; init; }

	/// <summary>How many written files hold a diagnostic.</summary>
	public required int FileCount { get; init; }
}

/// <summary>How many diagnostics carry one id, and how severe that id is.</summary>
public sealed record DiagnosticIdCount
{
	public required string Id { get; init; }

	public required string Severity { get; init; }

	public required int Count { get; init; }
}

/// <summary>How many diagnostics one project has.</summary>
public sealed record DiagnosticProjectCount
{
	public required string Project { get; init; }

	public required int Count { get; init; }
}

/// <summary>How many diagnostics one file has.</summary>
public sealed record DiagnosticFileCount
{
	public required string FilePath { get; init; }

	public required int Count { get; init; }
}
