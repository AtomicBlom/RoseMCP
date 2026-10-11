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

	/// <summary>How many of the newest matching entries to answer with, between one and <see cref="MaxLimit"/>.</summary>
	public int Limit { get; init; } = DefaultLimit;

	/// <summary>Whether each entry carries its arguments, response and diffs.</summary>
	public bool Bodies { get; init; }
}
