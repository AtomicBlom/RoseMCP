namespace RoseMcp.Worker;

/// <summary>
/// What the watcher noticed since the last barrier: what kind of trouble, and which files appeared.
/// <para>
/// The paths are here rather than left to the sweep because the sweep cannot find them. It stats the
/// documents it knows about, and a file that has just been created is not one of them -- so without
/// the watcher's own list the only way to notice a new file would be to enumerate the whole tree on
/// every read, which on a large repository costs more than everything else a barrier does.
/// </para>
/// </summary>
public sealed record WatchReport
{
	public static readonly WatchReport None = new();

	public WatchSignal Signal { get; init; }

	/// <summary>
	/// Files that appeared or were renamed into place. Best-effort, like every watcher signal: when
	/// too many arrive at once the report says <see cref="WatchSignal.FullResyncRequired"/> instead,
	/// and a reload finds them all.
	/// </summary>
	public IReadOnlyList<string> Created { get; init; } = [];

	public bool HasFlag(WatchSignal flag) => Signal.HasFlag(flag);
}
