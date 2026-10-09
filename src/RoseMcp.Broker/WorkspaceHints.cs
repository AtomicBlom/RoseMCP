namespace RoseMcp.Broker;

/// <summary>
/// Everything a call knows about which workspace it means, ranked by how much it actually knows.
/// <para>
/// This exists because the ranking used to be spelled out by hand at every tool: seventeen call
/// sites each writing <c>workspace ?? filePath</c>, one of them writing
/// <c>workspace ?? filePaths.FirstOrDefault()</c> instead, and a tool added later free to write
/// nothing at all. That is the same hazard attribution avoids by living in one place, so the
/// ranking lives in one place too and a tool only declares its inputs.
/// </para>
/// <para>
/// The two kinds are not interchangeable. <see cref="Workspace"/> is what the caller said, so a
/// value that resolves to nothing is a mistake they need to hear about. <see cref="Paths"/> are
/// inferred from arguments the caller supplied for another purpose entirely, so one that leads
/// nowhere is skipped rather than reported -- see <see cref="Paths"/> for why that matters.
/// </para>
/// <para>
/// Both are <see cref="RootedPath"/> rather than strings, which is what stops the resolution asking
/// the file system about a relative path: <c>File.Exists</c> and <c>Path.GetFullPath</c> answer
/// against the broker's own working directory, and a hint resolved there routes the call into
/// whichever checkout that process happens to be sitting in.
/// </para>
/// </summary>
public sealed record WorkspaceHints
{
	/// <summary>A call with nothing to go on, which resolves from the calling session's directory.</summary>
	public static readonly WorkspaceHints None = new();

	/// <summary>The workspace argument, named by the caller. Strict: it resolves or it fails.</summary>
	public RootedPath? Workspace { get; init; }

	/// <summary>
	/// The <c>workspaceKey</c> argument, quoted back from an earlier result. As strict as
	/// <see cref="Workspace"/>, and an alternative to it rather than a second opinion: a call naming
	/// both is refused, and a key no loaded workspace carries fails rather than falling through to the
	/// paths, because a hash cannot be turned back into the path it was taken from.
	/// </summary>
	public string? WorkspaceKey { get; init; }

	/// <summary>
	/// Paths the call named for its own reasons, best first, tried only if neither workspace argument
	/// was given.
	/// <para>
	/// Best-effort, and the reason is <c>rose_diagnostics</c>: its <c>target</c> is a file path under
	/// document scope and a <em>project name</em> under project scope. "Db.App" measured from the
	/// calling session's directory names nothing on disk, so it is passed over rather than followed
	/// -- and one that does name something there is a fact about the caller rather than an accident
	/// of where a process was started.
	/// </para>
	/// </summary>
	public IReadOnlyList<RootedPath?> Paths { get; init; } = [];

	/// <summary>
	/// Paths the call is going to create, tried after <see cref="Paths"/> and before the calling
	/// session's directory.
	/// <para>
	/// Kept apart from <see cref="Paths"/> because passing over what names nothing on disk is right for
	/// a hint that may not be a path at all, and wrong for one that names nothing by definition:
	/// <c>rose_add_file</c>'s <c>filePath</c> is the only argument saying where that call belongs, and
	/// treated as an ordinary hint it is always passed over, so a new file in another checkout is
	/// answered by the session's own workspace. One of these routes by its nearest existing ancestor
	/// instead, which is the directory the file will be placed under.
	/// </para>
	/// </summary>
	public IReadOnlyList<RootedPath?> Creating { get; init; } = [];

	/// <summary>
	/// The workspace argument, then any paths the call carries. Reads at the call site the way the
	/// hand-written <c>workspace ?? filePath</c> it replaces did.
	/// </summary>
	public static WorkspaceHints From(RootedPath? workspace, params RootedPath?[] paths) =>
		new() { Workspace = workspace, Paths = paths };

	/// <summary>
	/// What a tool was sent: the two ways of naming a workspace, then any paths the call carries.
	/// Every routed tool builds its hints here, and the unit suite calls each one with both names to
	/// prove the key reaches the routing rather than stopping at the parameter.
	/// </summary>
	public static WorkspaceHints From(RootedPath? workspace, string? workspaceKey, params RootedPath?[] paths) =>
		new() { Workspace = workspace, WorkspaceKey = Given(workspaceKey), Paths = paths };

	/// <summary>
	/// The two ways of naming a workspace, then a path the call will create, which is routed by where
	/// it is going rather than passed over for not being there yet.
	/// </summary>
	public static WorkspaceHints ForNewFile(RootedPath? workspace, string? workspaceKey, RootedPath? path) =>
		new() { Workspace = workspace, WorkspaceKey = Given(workspaceKey), Creating = [path] };

	/// <summary>
	/// A key that says something, or null. An empty string is what a client sends for an argument it
	/// filled in without meaning to, and treating it as a key would refuse a call that named nothing.
	/// </summary>
	private static string? Given(string? workspaceKey) =>
		string.IsNullOrWhiteSpace(workspaceKey) ? null : workspaceKey.Trim();

	/// <summary>
	/// The workspace argument, then a path the call will create, which is routed by where it is going
	/// rather than passed over for not being there yet.
	/// </summary>
	public static WorkspaceHints ForNewFile(RootedPath? workspace, RootedPath? path) =>
		new() { Workspace = workspace, Creating = [path] };

	/// <summary>
	/// Every path the call carries that says something about where it belongs, best first, each as
	/// the path routing asks about: the hint itself where it names something on disk, and a path the
	/// call will create by its nearest existing ancestor. A hint naming nothing on disk is left out,
	/// for the reason <see cref="Paths"/> gives.
	/// </summary>
	public IEnumerable<(RootedPath Hint, string Routed)> Routable()
	{
		foreach (var path in Paths)
		{
			if (path is null) continue;

			var exists = File.Exists(path.Value) || Directory.Exists(path.Value);
			if (exists) yield return (path, path.Value);
		}

		foreach (var path in Creating)
		{
			if (path is null) continue;

			if (NearestExisting(path.Value) is { } ancestor) yield return (path, ancestor);
		}
	}

	/// <summary>
	/// The path itself where it exists, else the nearest directory above it that does; null where not
	/// even its root does, as on a drive that is not there.
	/// </summary>
	private static string? NearestExisting(string path)
	{
		if (File.Exists(path) || Directory.Exists(path)) return path;

		for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
		{
			if (Directory.Exists(directory)) return directory;
		}

		return null;
	}
}
