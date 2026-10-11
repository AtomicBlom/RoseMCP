namespace RoseMcp.Contracts;

/// <summary>
/// Every debug session on the machine as <c>GET /operator/sessions</c> answers, whoever started it, with the
/// version of the view that a long poll waits past. Its own type rather than <see cref="LiveAppSessionList"/>,
/// which is what an agent's <c>rose_debug_list</c> answers and has no use for a version.
/// </summary>
public sealed record OperatorSessionList
{
	/// <summary>One row per open session, and per one dropped but still listed.</summary>
	public IReadOnlyList<LiveAppSessionSummary> Sessions { get; init; } = [];

	/// <summary>
	/// What the view held when it was read, as an opaque string: passed back as <c>after</c> with a
	/// <c>waitSeconds</c>, the next read waits until the view holds something else. How long a session has run
	/// and how old its report is are left out of it, since they change on every read.
	/// </summary>
	public required string Version { get; init; }
}
