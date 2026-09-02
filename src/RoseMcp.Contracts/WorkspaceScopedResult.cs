namespace RoseMcp.Contracts;

/// <summary>
/// The base every tool result shares: which workspace produced it.
/// <para>
/// A result that does not say where it came from cannot be checked. A symbol search of the wrong
/// solution returns nothing, and nothing is exactly what a search of the right solution returns
/// when the symbol genuinely is not there -- so a caller reading a zero has no way to tell a true
/// negative from an answer about a repository it never asked about. Naming the workspace is what
/// makes the difference visible without the caller having to suspect it first.
/// </para>
/// <para>
/// Filled in by the broker rather than the worker. The broker is what chose the workspace, so it is
/// the only party that can say so without being asked, and doing it in one place means a tool added
/// later cannot forget to.
/// </para>
/// </summary>
public abstract record WorkspaceScopedResult
{
	/// <summary>Absolute path of the solution, or bare project, that answered.</summary>
	public string Workspace { get; init; } = string.Empty;

	/// <summary>
	/// Short stable name for that workspace, safe to pass back as the <c>workspace</c> argument.
	/// Survives the worker being restarted, because it is derived from the path.
	/// </summary>
	public string WorkspaceKey { get; init; } = string.Empty;
}
