namespace RoseMcp.Contracts;

/// <summary>What ensuring a set of imports did to one file.</summary>
public sealed record UsingResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	public required string FilePath { get; init; }

	/// <summary>Namespaces written into the file.</summary>
	public required IReadOnlyList<string> Added { get; init; }

	/// <summary>
	/// Namespaces that needed nothing, each with the reason. Said rather than left out, because
	/// "already imported here" and "in scope from a global using you cannot see in this file" look
	/// identical from the outside, and the second is the one that makes a caller doubt the answer.
	/// </summary>
	public IReadOnlyList<string> AlreadyInScope { get; init; } = [];

	public required bool Applied { get; init; }

	public required string Diff { get; init; }

	public required bool Verified { get; init; }

	/// <summary>
	/// Errors this brought into being. An import usually only resolves things, but it can make a
	/// name ambiguous between two namespaces -- which is a compile error the caller has to settle.
	/// </summary>
	public IReadOnlyList<DiagnosticEntry> IntroducedDiagnostics { get; init; } = [];

	/// <summary>How many errors it made go away, which for this operation is the point.</summary>
	public int ResolvedDiagnosticCount { get; init; }

	public int TotalErrorCount { get; init; }

	public IReadOnlyList<string> ProjectsChecked { get; init; } = [];
}
