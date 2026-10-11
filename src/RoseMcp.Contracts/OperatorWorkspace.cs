namespace RoseMcp.Contracts;

/// <summary>
/// One workspace as a person's window shows it, read at <c>GET /operator/workspaces</c>: the row the tray reads,
/// with where the solution is, what it was loaded under, when it was last used and when it will be closed.
/// <para>
/// The row wraps a <see cref="WorkspaceSummary"/> rather than adding to it, because that summary is also what
/// <c>rose_workspace_open</c> answers an agent, and an agent has no use for a folder link or a close time.
/// </para>
/// </summary>
public sealed record OperatorWorkspace
{
	/// <summary>The row the tray reads: state, health with what the person accepted, memory and what is running.</summary>
	public required WorkspaceSummary Summary { get; init; }

	/// <summary>The directory the solution file is in, for a window to open.</summary>
	public required string Folder { get; init; }

	/// <summary>
	/// The MSBuild configuration the solution was loaded under, from the worker's last status report; until it
	/// reports, the one the worker was started with where one was asked for, and null otherwise.
	/// </summary>
	public string? Configuration { get; init; }

	/// <summary>The MSBuild platform the solution was loaded under, read the way <see cref="Configuration"/> is.</summary>
	public string? Platform { get; init; }

	/// <summary>
	/// Further MSBuild properties the worker was started with, as <c>Name=Value</c>: what decides the target
	/// framework where the configuration does not.
	/// </summary>
	public IReadOnlyList<string> BuildProperties { get; init; } = [];

	/// <summary>
	/// When a tool call last used it, or when its load finished where none has. Listing, status and these
	/// reads do not count, so watching a workspace does not keep it warm.
	/// </summary>
	public required DateTime LastUsedUtc { get; init; }

	/// <summary>How long since <see cref="LastUsedUtc"/>, as of the read.</summary>
	public required TimeSpan IdleFor { get; init; }

	/// <summary>
	/// Whether something holds it now -- a call, or its load -- so it is in use and not closing whatever its
	/// idle time.
	/// </summary>
	public required bool InUse { get; init; }

	/// <summary>
	/// About when the broker closes it to free its memory if nothing uses it first: its last use plus the idle
	/// limit, or sooner where its solution file has gone. Null while it is in use, once it has stopped, and
	/// where nothing is closed for being idle.
	/// </summary>
	public DateTime? ClosesAtUtc { get; init; }

	/// <summary>
	/// The analyzer, generator and code-fix assemblies the worker's reads found rebuilt since it loaded them, by
	/// full path: answers come from the old build until a reload.
	/// </summary>
	public IReadOnlyList<string> RebuiltAnalyzers { get; init; } = [];
}
