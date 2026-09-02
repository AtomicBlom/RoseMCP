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

	public required DateTime StartedUtc { get; init; }

	public required TimeSpan Uptime { get; init; }

	/// <summary>Why the session is faulted, when it is.</summary>
	public string? Detail { get; init; }

	public IReadOnlyList<WorkerActivity> Running { get; init; } = [];

	public IReadOnlyList<WorkerActivity> Recent { get; init; } = [];
}
