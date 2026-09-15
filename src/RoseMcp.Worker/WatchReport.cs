namespace RoseMcp.Worker;

/// <summary>
/// What the watcher noticed since the last barrier: what kind of trouble, and which build files moved.
/// <para>
/// Build files only. The sweep finds every tracked document that changed and the directory walk every
/// source file that appeared, whatever the count, so neither needs the event stream. A build file is
/// different: one appearing is invisible to both, and one changing that the sweep does not track is
/// still a reason to reload when a project could not be evaluated and might import it.
/// </para>
/// </summary>
public sealed record WatchReport
{
	public static readonly WatchReport None = new();

	public WatchSignal Signal { get; init; }

	/// <summary>Build files that appeared or were renamed into place.</summary>
	public IReadOnlyList<string> BuildFilesAppeared { get; init; } = [];

	/// <summary>Build files that changed, were removed, or were renamed away.</summary>
	public IReadOnlyList<string> BuildFilesChanged { get; init; } = [];

	public bool HasFlag(WatchSignal flag) => Signal.HasFlag(flag);
}
