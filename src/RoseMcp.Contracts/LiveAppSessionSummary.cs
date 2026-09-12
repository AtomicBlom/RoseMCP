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

	/// <summary>
	/// Something the broker has to say about this session that the host did not, and that is not a
	/// fault: an inspector that was asked for and could not open is the case it exists for.
	/// <para>
	/// Kept apart from <see cref="Detail"/> on purpose. Detail means the session is broken and its
	/// answers cannot be trusted; this means the session is fine and something beside it is not.
	/// Folding the two would make every such notice read as a debugger that had failed.
	/// </para>
	/// </summary>
	public string? Notice { get; init; }

	public IReadOnlyList<WorkerActivity> Running { get; init; } = [];

	public IReadOnlyList<WorkerActivity> Recent { get; init; } = [];

	/// <summary>Whether the target is executing, and if not, what stopped it.</summary>
	public required LiveExecutionState Execution { get; init; }

	/// <summary>The stop being held, when the target is not running. Null exactly when it is.</summary>
	public LiveStop? Stop { get; init; }

	/// <summary>Which XAML framework the target is running, read from its loaded modules.</summary>
	public required XamlStack XamlStack { get; init; }

	/// <summary>The sentence that decided <see cref="XamlStack"/>, naming the modules it read.</summary>
	public required string XamlStackReason { get; init; }

	/// <summary>Whether a diagnostics provider is resident, which decides what the next XAML read costs.</summary>
	public required LiveXamlProvider XamlProvider { get; init; }

	/// <summary>
	/// The log file the host is writing, so a reader can open the log that explains this session.
	/// </summary>
	public string? HostLogPath { get; init; }

	/// <summary>
	/// How long ago the target last produced a debug event, or null when it has produced none.
	/// <para>
	/// An age rather than the moment, computed here alongside <see cref="Uptime"/>, because the two
	/// answer the same kind of question and a reader comparing them should not have to notice that one
	/// was subtracted for it and the other was not.
	/// </para>
	/// </summary>
	public TimeSpan? LastEventAge { get; init; }

	/// <summary>
	/// How stale the host's self-report is: everything above except the activity lists comes from a
	/// poll, so this is how long ago that poll answered. Null before the first one has.
	/// <para>
	/// Reported because the fields it qualifies are the ones a reader acts on. A session whose host has
	/// stopped answering keeps describing itself accurately as of some moment, and without this there
	/// is nothing to say which moment that was.
	/// </para>
	/// </summary>
	public TimeSpan? InfoAge { get; init; }
}
