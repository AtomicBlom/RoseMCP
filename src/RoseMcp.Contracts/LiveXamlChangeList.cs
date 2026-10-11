namespace RoseMcp.Contracts;

/// <summary>
/// The live edits made to one debug session's app, as <c>GET /operator/sessions/{id}/xaml/changes</c> answers, with
/// the version a long poll waits past.
/// </summary>
public sealed record LiveXamlChangeList
{
	/// <summary>The session the changes were made in.</summary>
	public required string SessionId { get; init; }

	/// <summary>Every change kept for the session, oldest first.</summary>
	public IReadOnlyList<LiveXamlChange> Changes { get; init; } = [];

	/// <summary>
	/// How many older changes were let go to keep the list bounded, so a list that starts part way through says so
	/// rather than reading as everything that was done.
	/// </summary>
	public long Dropped { get; init; }

	/// <summary>
	/// What the view held when it was read, as an opaque string: passed back as <c>after</c> with a
	/// <c>waitSeconds</c>, the next read waits until another change is made.
	/// </summary>
	public required string Version { get; init; }
}
