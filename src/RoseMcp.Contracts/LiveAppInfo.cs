namespace RoseMcp.Contracts;

/// <summary>
/// What a live-app host can say about itself and its target, cheaply. The broker asks on connect,
/// the way it asks a worker for <see cref="WorkerInfo"/>, so it learns the host's process id and the
/// architecture it launched as without waiting on any real work.
/// </summary>
public sealed record LiveAppInfo
{
	/// <summary>The host process's own id (not the target's).</summary>
	public required int HostProcessId { get; init; }

	/// <summary>The architecture the host launched as, which is the target's architecture.</summary>
	public required TargetArchitecture Architecture { get; init; }

	public required LiveAppSessionState State { get; init; }

	/// <summary>The target process id, once known.</summary>
	public int? TargetProcessId { get; init; }

	/// <summary>
	/// Where the packaged app this session activated is installed from, for a UWP target; null for
	/// anything else, and null when it could not be read.
	/// <para>
	/// Reported because a registration can point somewhere other than the layout that was just
	/// built, and nothing else in a session's answers would give that away. Everything downstream
	/// then describes the wrong build accurately -- the XAML tools worst of all, since they hand back
	/// source file and line provenance into files whose current contents no longer correspond to what
	/// is running.
	/// </para>
	/// </summary>
	public string? InstallLocation { get; init; }

	/// <summary>Why the session is faulted, when it is.</summary>
	public string? Detail { get; init; }

	/// <summary>Whether the target is executing, and if not, what stopped it.</summary>
	public required LiveExecutionState Execution { get; init; }

	/// <summary>The stop being held, when the target is not running. Null exactly when it is.</summary>
	public LiveStop? Stop { get; init; }

	/// <summary>
	/// Which XAML framework the target is running, read from its loaded modules.
	/// <para>
	/// Reported on the cheap self-report rather than only from a XAML call, because it is what decides
	/// whether a XAML surface can be offered at all -- and asking a XAML tool to find out means paying
	/// for an injection to learn that no injection was possible.
	/// </para>
	/// </summary>
	public required XamlStack XamlStack { get; init; }

	/// <summary>
	/// The sentence that decided <see cref="XamlStack"/>, naming the modules it read. Carried because
	/// <see cref="Contracts.XamlStack.Unknown"/> has two causes worth telling apart -- modules that
	/// could not be read at all, and modules read and recognised as nothing -- and the first is
	/// ignorance where the second is a finding.
	/// </summary>
	public required string XamlStackReason { get; init; }

	/// <summary>Whether a diagnostics provider is resident, which decides what the next XAML read costs.</summary>
	public required LiveXamlProvider XamlProvider { get; init; }

	/// <summary>
	/// When the target last produced a debug event. Null only when it has produced none, which is a
	/// session that has not finished establishing.
	/// </summary>
	public LiveHeartbeat? LastEvent { get; init; }

	/// <summary>
	/// The log file this host is writing, so a reader looking at a session can open the log that
	/// explains it rather than guessing which of twenty files in the folder is the one.
	/// </summary>
	public string? HostLogPath { get; init; }
}
