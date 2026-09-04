namespace RoseMcp.Contracts;

/// <summary>
/// Outcome of changing a member's parameters: which declarations moved with it, which call sites
/// were rewritten, which were not, and what it broke.
/// </summary>
public sealed record SignatureChangeResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	/// <summary>The member as it was, so the caller can see which one this resolved to.</summary>
	public required string Symbol { get; init; }

	/// <summary>The parameter list as it now reads.</summary>
	public required string Parameters { get; init; }

	public required bool Applied { get; init; }

	public required string Diff { get; init; }

	/// <summary>
	/// Every declaration the change was applied to: the member named, its base declaration, and
	/// everything overriding or implementing it. More than one is the normal case for a virtual or
	/// interface member, and changing only the one named would not compile.
	/// </summary>
	public required IReadOnlyList<SourceLocation> UpdatedDeclarations { get; init; }

	/// <summary>Call sites whose arguments were rewritten.</summary>
	public required IReadOnlyList<SourceLocation> UpdatedCallSites { get; init; }

	/// <summary>
	/// Call sites left as they were, each with the reason. Worth reading even when the change
	/// compiles: a caller still passing the old default is a decision, not a fact.
	/// </summary>
	public IReadOnlyList<UnchangedCallSite> UnchangedCallSites { get; init; } = [];

	/// <summary>Documentation comments whose param tags were brought into line.</summary>
	public IReadOnlyList<string> DocumentationUpdated { get; init; } = [];

	/// <summary>Whether anything was compiled afterwards.</summary>
	public required bool Verified { get; init; }

	/// <summary>
	/// Errors that exist now and did not before. The whole solution is checked rather than the
	/// projects holding the file, because a missed call site is by definition somewhere this did not
	/// look, and it is the failure the tool exists to prevent.
	/// </summary>
	public IReadOnlyList<DiagnosticEntry> IntroducedDiagnostics { get; init; } = [];

	public int ResolvedDiagnosticCount { get; init; }

	public int TotalErrorCount { get; init; }

	public IReadOnlyList<string> ProjectsChecked { get; init; } = [];
}
