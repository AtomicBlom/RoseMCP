namespace RoseMcp.Contracts;

/// <summary>
/// Outcome of writing C# by symbol: what was written, where it landed, and what it broke.
/// <para>
/// The diagnostics are the part that changes how a caller works. Writing and then verifying used to
/// be two round trips with a build in between -- fourteen to twenty-five seconds each time, paid
/// dozens of times in one session while a warm compilation sat unused. Answering both in one call is
/// what makes an edit-and-check loop cheap enough to run after every change rather than at the end.
/// </para>
/// </summary>
public sealed record MemberEditResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	/// <summary>
	/// The declaration this call resolved to, spelled as the compilation spells it. Worth reading
	/// back: it is the proof that the name in the request found the member that was meant.
	/// </summary>
	public required string Symbol { get; init; }

	/// <summary>The file the code was written into.</summary>
	public required string FilePath { get; init; }

	/// <summary>
	/// One-based line where the written declaration now starts, so a following call can point at it
	/// without reading the file back.
	/// </summary>
	public required int Line { get; init; }

	/// <summary>Names of the members written, in the order they appear.</summary>
	public required IReadOnlyList<string> Members { get; init; }

	/// <summary>False when this was a preview; nothing was written.</summary>
	public required bool Applied { get; init; }

	/// <summary>Unified diff of the change, so the caller can see the edit rather than trust it.</summary>
	public required string Diff { get; init; }

	/// <summary>
	/// Whether the projects holding this file were compiled after the edit.
	/// <para>
	/// Reported rather than assumed, because an empty <see cref="IntroducedDiagnostics"/> means
	/// nothing at all when nothing was compiled -- and reads exactly like a clean result.
	/// </para>
	/// </summary>
	public required bool Verified { get; init; }

	/// <summary>
	/// Errors that exist now and did not before, which is a different question from what the file
	/// reports. A signature change breaks its call sites, and those are in other files: the one
	/// failure in the session behind this tool that reached a build undetected was a missed call site
	/// found only from CS7036.
	/// </summary>
	public IReadOnlyList<DiagnosticEntry> IntroducedDiagnostics { get; init; } = [];

	/// <summary>How many errors the edit made go away. Useful when the edit was the fix.</summary>
	public int ResolvedDiagnosticCount { get; init; }

	/// <summary>
	/// Every error the checked projects report now, this edit's and everyone else's. A project that
	/// was already failing does not become this call's fault, and cannot be reported as though it did.
	/// </summary>
	public int TotalErrorCount { get; init; }

	/// <summary>
	/// The projects that were compiled: the ones that hold this file, not the ones that depend on
	/// them. Named so the caller knows the scope of the answer rather than guessing at it.
	/// </summary>
	public IReadOnlyList<string> ProjectsChecked { get; init; } = [];
}
