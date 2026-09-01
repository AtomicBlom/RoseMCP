namespace RoseMcp.Worker;

/// <summary>Which diagnostic to fix, over how much of the solution.</summary>
public sealed record CodeFixRequest
{
	/// <summary>The diagnostic id to fix, for example CA1822.</summary>
	public required string DiagnosticId { get; init; }

	/// <summary>
	/// document, project, or solution. A document scope still needs a file to start from; the wider
	/// scopes take theirs from that file's project.
	/// </summary>
	public string Scope { get; init; } = "document";

	/// <summary>A file in the scope to fix. Required, because the fix has to start somewhere.</summary>
	public required string FilePath { get; init; }

	/// <summary>
	/// Which fix, when a diagnostic offers several. Matched against the fix titles, and the first is
	/// taken when unset -- reported either way, so a caller can see which one ran.
	/// </summary>
	public string? FixTitle { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
