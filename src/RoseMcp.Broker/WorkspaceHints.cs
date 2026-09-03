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
/// </summary>
public sealed record WorkspaceHints
{
	/// <summary>A call with nothing to go on, which resolves from the calling session's directory.</summary>
	public static readonly WorkspaceHints None = new();

	/// <summary>The workspace argument, named by the caller. Strict: it resolves or it fails.</summary>
	public string? Workspace { get; init; }

	/// <summary>
	/// Paths the call named for its own reasons, best first, tried only if the workspace argument was
	/// omitted.
	/// <para>
	/// Best-effort, and the reason is <c>rose_diagnostics</c>: its <c>target</c> is a file path under
	/// document scope and a <em>project name</em> under project scope. Resolving "Db.App" as a path
	/// makes it relative to the process working directory, which for the tray is its own install
	/// directory and for anyone else is a directory that has nothing to do with the question -- so a
	/// hint that names nothing on disk is passed over rather than followed somewhere arbitrary.
	/// </para>
	/// </summary>
	public IReadOnlyList<string?> Paths { get; init; } = [];

	/// <summary>
	/// The workspace argument, then any paths the call carries. Reads at the call site the way the
	/// hand-written <c>workspace ?? filePath</c> it replaces did.
	/// </summary>
	public static WorkspaceHints From(string? workspace, params string?[] paths) =>
		new() { Workspace = workspace, Paths = paths };
}
