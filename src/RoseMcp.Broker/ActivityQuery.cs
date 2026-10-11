namespace RoseMcp.Broker;

/// <summary>
/// Which entries of the activity record to read: a client's, a workspace's or a debug session's, a tool's, in
/// a window of time, up to a limit, with or without their bodies. Every filter left null matches everything.
/// </summary>
public sealed record ActivityQuery
{
	/// <summary>How many entries a query answers with when it does not say.</summary>
	public const int DefaultLimit = 100;

	/// <summary>The most a query answers with, however many it asks for.</summary>
	public const int MaxLimit = 1000;

	/// <summary>
	/// The most a query answers with when it asks for bodies: each body can be tens of kilobytes, so a page of them is
	/// kept to what a window shows at once.
	/// </summary>
	public const int MaxLimitWithBodies = 50;

	/// <summary>A client, by <see cref="Contracts.ActivityEntry.ClientId"/> or <see cref="Contracts.ActivityEntry.ClientKey"/>.</summary>
	public string? Client { get; init; }

	/// <summary>A workspace, by its solution path or its key.</summary>
	public string? Workspace { get; init; }

	/// <summary>A debug session, by its id.</summary>
	public string? DebugSession { get; init; }

	/// <summary>A tool, by its name.</summary>
	public string? Tool { get; init; }

	/// <summary>Only entries that started at or after this.</summary>
	public DateTimeOffset? Since { get; init; }

	/// <summary>Only entries that started before this.</summary>
	public DateTimeOffset? Until { get; init; }

	/// <summary>Only calls that failed: the caller was told something went wrong. A broker starting is not one.</summary>
	public bool Failures { get; init; }

	/// <summary>
	/// Only entries naming this, ignoring case: in the tool, the target, the error, the client's name or label, the
	/// directory it works in, the workspace or the debug session. What a listing carries, and not the bodies, which
	/// would have to be read back from disk for every entry to be searched.
	/// </summary>
	public string? Text { get; init; }

	/// <summary>
	/// Only entries older than where this cursor points: the <see cref="Contracts.ActivityPage.Next"/> of the page
	/// before. Null for the first page.
	/// </summary>
	public string? Before { get; init; }

	/// <summary>
	/// How many of the newest matching entries to answer with, between one and <see cref="MaxLimit"/>, or
	/// <see cref="MaxLimitWithBodies"/> where <see cref="Bodies"/> asks for them.
	/// </summary>
	public int Limit { get; init; } = DefaultLimit;

	/// <summary>Whether each entry carries its arguments, response and diffs.</summary>
	public bool Bodies { get; init; }
}
