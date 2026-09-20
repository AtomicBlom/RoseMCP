namespace RoseMcp.Broker;

/// <summary>
/// Everything a call knows about which workspace it means, ranked by how much it actually knows.
/// <para>
/// This exists because the ranking used to be spelled out by hand at every tool: seventeen call
/// sites each writing <c>workspace ?? filePath</c>, one of them writing
/// <c>workspace ?? filePaths.FirstOrDefault()</c> instead, and a tool added later free to write
/// nothing at all. That is the same hazard attribution avoids by living in one place, so the
/// ranking lives in one place too and a tool only declares its inputs.
/// </para>
/// <para>
/// The two kinds are not interchangeable. <see cref="Workspace"/> is what the caller said, so a
/// value that resolves to nothing is a mistake they need to hear about. <see cref="Paths"/> are
/// inferred from arguments the caller supplied for another purpose entirely, so one that leads
/// nowhere is skipped rather than reported -- see <see cref="Paths"/> for why that matters.
/// </para>
/// <para>
/// Both are <see cref="RootedPath"/> rather than strings, which is what stops the resolution asking
/// the file system about a relative path: <c>File.Exists</c> and <c>Path.GetFullPath</c> answer
/// against the broker's own working directory, and a hint resolved there routes the call into
/// whichever checkout that process happens to be sitting in.
/// </para>
/// </summary>
public sealed record WorkspaceHints
{
	/// <summary>A call with nothing to go on, which resolves from the calling session's directory.</summary>
	public static readonly WorkspaceHints None = new();

	/// <summary>The workspace argument, named by the caller. Strict: it resolves or it fails.</summary>
	public RootedPath? Workspace { get; init; }

	/// <summary>
	/// Paths the call named for its own reasons, best first, tried only if the workspace argument was
	/// omitted.
	/// <para>
	/// Best-effort, and the reason is <c>rose_diagnostics</c>: its <c>target</c> is a file path under
	/// document scope and a <em>project name</em> under project scope. "Db.App" measured from the
	/// calling session's directory names nothing on disk, so it is passed over rather than followed
	/// -- and one that does name something there is a fact about the caller rather than an accident
	/// of where a process was started.
	/// </para>
	/// </summary>
	public IReadOnlyList<RootedPath?> Paths { get; init; } = [];

	/// <summary>
	/// The workspace argument, then any paths the call carries. Reads at the call site the way the
	/// hand-written <c>workspace ?? filePath</c> it replaces did.
	/// </summary>
	public static WorkspaceHints From(RootedPath? workspace, params RootedPath?[] paths) =>
		new() { Workspace = workspace, Paths = paths };
}
