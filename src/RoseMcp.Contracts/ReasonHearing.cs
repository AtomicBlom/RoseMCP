namespace RoseMcp.Contracts;

/// <summary>
/// How the call a worker is answering is to hear one kind of reason for one project: what the person's
/// acceptance or the calling session's acknowledgement says agents read about it.
/// <para>
/// Sent with the call in <c>_meta</c> under <see cref="MetaKey"/>, because the decision is split across two
/// processes. Whether a read says its workspace is degraded is decided in the worker, which is the only one
/// that sees a reload it made on its own or an assembly a tool failed to load a call ago; what the person
/// accepted and what this session acknowledged is known only to the broker, which outlives every worker and
/// is the only one that can tell sessions apart. So the broker sends what it knows with each call, and the
/// worker leaves out what the caller has been told not to hear -- keyed on kind and project, so a reason the
/// list does not cover entirely is said as it always is.
/// </para>
/// </summary>
public sealed record ReasonHearing
{
	/// <summary>Where in a tool call's <c>_meta</c> the broker puts the list, beside the correlation id and the origin directory.</summary>
	public const string MetaKey = "rosemcp/heardReasons";

	/// <summary>The kind, one of <see cref="ReasonKinds"/>.</summary>
	public required string Kind { get; init; }

	/// <summary>What it covers: a project by name, or, for a reason that names no project, its subject -- the assembly that would not load, the platform chosen.</summary>
	public string Covering { get; init; } = string.Empty;

	/// <summary>What the call reads about it.</summary>
	public required AgentsRead Read { get; init; }

	/// <summary>The person's note, where <see cref="Read"/> is <see cref="AgentsRead.Note"/>.</summary>
	public string? Note { get; init; }
}
