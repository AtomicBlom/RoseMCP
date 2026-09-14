namespace RoseMcp.Contracts;

/// <summary>
/// Everything a caller needs to decide whether to trust this workspace's answers. Deliberately
/// verbose about failure: a workspace that loaded but cannot see its source generators produces
/// confidently wrong results, so those causes are reported rather than left to be inferred from
/// suspiciously empty output.
/// </summary>
public sealed record WorkspaceStatusReport : WorkspaceScopedResult
{
	public required string SolutionPath { get; init; }

	public required WorkspaceState State { get; init; }

	/// <summary>
	/// Monotonic snapshot counter. Bumped by every mutation, absorbed file change, and reload.
	/// Two results carrying the same revision describe the same world.
	/// </summary>
	public required long Revision { get; init; }

	public required IReadOnlyList<ProjectStatus> Projects { get; init; }

	/// <summary>
	/// Diagnostics MSBuild reported while loading -- unresolved references, failed projects -- with
	/// complaints that differ only in the file or URL they name folded into one line carrying a count.
	/// <para>
	/// Folded because they are otherwise most of the answer. On a 60-project solution this field was
	/// 196KB of a 225KB report, and 509 of its 557 entries were one message: NuGet's vulnerability
	/// audit failing to reach a feed, repeated once per project per feed. That is one fact about the
	/// solution, not 509 facts, and it put every call over the client's token cap.
	/// </para>
	/// <para>
	/// <see cref="LoadDiagnosticCount"/> is what MSBuild actually said, so the folding cannot
	/// understate it.
	/// </para>
	/// </summary>
	public required IReadOnlyList<string> LoadDiagnostics { get; init; }

	/// <summary>
	/// How many diagnostics MSBuild reported, before <see cref="LoadDiagnostics"/> folded the
	/// repetitions together. Equal to that list's length when nothing repeated.
	/// </summary>
	public int LoadDiagnosticCount { get; init; }

	/// <summary>
	/// Why this workspace is <see cref="WorkspaceState.Degraded"/>, each paired with the command
	/// that would fix it. Empty when the load is trustworthy.
	/// <para>
	/// One reason per kind, never one per instance. A kind's remedy is a property of the kind, so
	/// nine analyzers that will not load are one reason naming nine assemblies rather than nine
	/// reasons each repeating the same two sentences about what to do. The tray draws this whole list
	/// into a single information bar, which makes its length the panel's height, and a wall of
	/// near-identical paragraphs is read as one thing and skipped -- the same emptying of the word
	/// that keeping stale build output out of here avoids.
	/// </para>
	/// <para>
	/// Folded reasons name what they cover and leave the particulars to the fields that already hold
	/// them: <see cref="ProjectStatus.UnresolvedXamlTypes"/>, <see cref="ProjectStatus.MissingAnalyzerOutputs"/>,
	/// <see cref="AnalyzerLoadFailures"/> and <see cref="RestoreReport.Unrestored"/>. So a reason is
	/// an index into this report rather than a second copy of it, and nothing is lost by shortening
	/// it.
	/// </para>
	/// </summary>
	public required IReadOnlyList<string> DegradedReasons { get; init; }

	/// <summary>
	/// Every analyzer or generator assembly that would not load, with the message the runtime gave.
	/// <see cref="DegradedReasons"/> folds these to one line and names the assemblies; this is where
	/// the version each load wanted, and the HRESULT it failed with, survive that fold.
	/// </summary>
	public IReadOnlyList<AnalyzerLoadFailure> AnalyzerLoadFailures { get; init; } = [];

	/// <summary>
	/// The MSBuild configuration, platform and any pinned properties this workspace was loaded
	/// under, as <c>Configuration|Platform (Name=Value)</c>.
	/// <para>
	/// Worth reporting even when it is the default, because it decides what every project's target
	/// framework resolves to. A solution loaded under a configuration it does not declare produces a
	/// project with no framework and no references, and the diagnostics that follow describe
	/// everything except the cause.
	/// </para>
	/// </summary>
	public string? BuildConfiguration { get; init; }

	/// <summary>
	/// Configurations the solution declares, when it declares any. Present so a caller that does not
	/// like the one in use can name another without going to read the solution file.
	/// </summary>
	public IReadOnlyList<string> AvailableConfigurations { get; init; } = [];

	/// <summary>
	/// Things worth knowing that are not failures -- chiefly a configuration nobody asked for having
	/// been chosen. Not degraded reasons: the load is trustworthy, it just made a decision.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];

	public RestoreReport? Restore { get; init; }

	public double LoadSeconds { get; init; }
}
