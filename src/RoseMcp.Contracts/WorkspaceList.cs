namespace RoseMcp.Contracts;

/// <summary>
/// The workspaces a broker holds a worker for, and the rule that decides how long each stays warm.
/// <para>
/// A wrapper because MCP requires a tool's <c>structuredContent</c> to be a JSON object: a tool
/// returning a bare collection serialises to a top-level array, which fails client-side schema
/// validation and makes the tool uncallable.
/// </para>
/// <para>
/// Not a <see cref="WorkspaceScopedResult"/>, because it answers about every workspace rather than
/// one, and each row names its own. A root naming one of them, or none with an empty string, would
/// be attribution that says something false.
/// </para>
/// </summary>
public sealed record WorkspaceList
{
	/// <summary>One row per worker the broker holds, running or stopped, ordered by path.</summary>
	public IReadOnlyList<WorkspaceListEntry> Workspaces { get; init; } = [];

	/// <summary>
	/// How long a worker may sit unused before it is stopped to free its memory. Null where nothing
	/// is evicted, which is a broker serving one client over stdio: its workers end with that client.
	/// </summary>
	public TimeSpan? IdleEvictionAfter { get; init; }
}
