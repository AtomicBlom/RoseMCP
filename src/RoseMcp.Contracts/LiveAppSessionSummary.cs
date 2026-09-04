namespace RoseMcp.Contracts;

/// <summary>
/// One row of the admin view for a live-app session, the debugging counterpart to
/// <see cref="WorkspaceSummary"/>. The same model backs any UI and GET /admin/sessions, so they
/// cannot disagree.
/// </summary>
public sealed record LiveAppSessionSummary
{
	/// <summary>Stable id the broker assigned this session.</summary>
	public required string SessionId { get; init; }

	public required string TargetDescription { get; init; }

	public required TargetArchitecture Architecture { get; init; }

	public required LiveAppSessionState State { get; init; }

	/// <summary>The host process's id, for sampling its memory from outside.</summary>
	public int? HostProcessId { get; init; }

	public int? TargetProcessId { get; init; }

	/// <summary>
	/// Where the packaged app this session activated is installed from, for a UWP target. Null
	/// otherwise, and null when it could not be read.
	/// <para>
	/// It is on the summary rather than only in the event stream because a caller that reads one
	/// result and then works for an hour never goes back to the events, and a stale registration is
	/// invisible in every other field.
	/// </para>
	/// </summary>
	public string? InstallLocation { get; init; }

	public required DateTime StartedUtc { get; init; }

	public required TimeSpan Uptime { get; init; }

	/// <summary>Why the session is faulted, when it is.</summary>
	public string? Detail { get; init; }

	public IReadOnlyList<WorkerActivity> Running { get; init; } = [];

	public IReadOnlyList<WorkerActivity> Recent { get; init; } = [];
}
