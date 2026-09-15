namespace RoseMcp.Solutions;

/// <summary>
/// The files that change how a project evaluates rather than what is in it.
/// <para>
/// Three lists of these had drifted apart, and folding them into one would have been the wrong fix:
/// they answer different questions. What invalidates a restore is not what a snapshot cannot
/// represent -- an <c>.editorconfig</c> decides what the analyzers and the formatter do to every
/// file beneath it and has nothing to say about package resolution, so putting it in the restore
/// list would re-run NuGet every time somebody changed a formatting rule.
/// </para>
/// <para>
/// So it is two named sets with one of them built from the other, which keeps them from drifting
/// while leaving the difference stated rather than lost.
/// </para>
/// </summary>
public static class BuildInfluencingFiles
{
	/// <summary>
	/// Files that change how projects evaluate without appearing in any project, found by walking up
	/// from a solution or a project. Editing <c>Directory.Packages.props</c> rewrites the reference graph
	/// while every csproj stays untouched.
	/// <para>
	/// This is also exactly the set that invalidates a restore, which is why the restore inputs are this
	/// and not <see cref="Structural"/>.
	/// </para>
	/// <para>
	/// One canonical spelling each. A caller that probes these by name gets whatever its filesystem
	/// matches, which on Windows is every casing and on a case-sensitive one is this spelling alone;
	/// a caller comparing a name it already has uses <see cref="IsAmbient"/>, which ignores case
	/// everywhere.
	/// </para>
	/// </summary>
	public static readonly IReadOnlyList<string> Ambient =
	[
		"Directory.Build.props",
		"Directory.Build.targets",
		"Directory.Packages.props",
		"global.json",
		"nuget.config",
	];

	/// <summary>
	/// Files whose appearance or change cannot be patched into a snapshot, so the session reloads
	/// instead. <see cref="Ambient"/> plus the three that are read per directory rather than up a
	/// tree.
	/// <para>
	/// Named one by one rather than matched by extension: any new <c>.json</c> would otherwise force
	/// a reload, and an agent writing code creates those for reasons that have nothing to do with the
	/// build.
	/// </para>
	/// </summary>
	public static readonly IReadOnlySet<string> Structural =
		new HashSet<string>([.. Ambient, ".editorconfig", "packages.config", WorkspaceConfigFile.FileName], StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Whether a file name is one of <see cref="Ambient"/>, compared without case.
	/// <para>
	/// Asked rather than matched against the list directly, because the list holds one spelling and
	/// Windows has several. Three casings of <c>nuget.config</c> sat in one of the lists this
	/// replaces, which is redundant on a case-insensitive filesystem and still incomplete on one
	/// that is not.
	/// </para>
	/// </summary>
	public static bool IsAmbient(string fileName) =>
		Ambient.Any(name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Whether a path names a build file: a project or solution file, a <c>.props</c> or <c>.targets</c>,
	/// or one of <see cref="Structural"/>.
	/// <para>
	/// This is what a file watcher remembers, and nothing else. Every read already stats each tracked
	/// document and walks the project directories for new source files, so a source file changing -- or
	/// a thousand of them -- needs nothing from the event stream, and a list holding every event has to
	/// be capped, which turns the number of events into a reason to reload. A build file is the one kind
	/// a read cannot find for itself when it appears.
	/// </para>
	/// </summary>
	public static bool IsBuildFile(string path)
	{
		if (Structural.Contains(Path.GetFileName(path))) return true;

		return IsImportable(path)
			|| Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" or ".slnf";
	}

	/// <summary>
	/// Whether a path could be imported into a project: a <c>.props</c> or a <c>.targets</c>. Which of
	/// these a project really imports is learned by evaluating it, so this is the whole answer only for a
	/// project that could not be evaluated.
	/// </summary>
	public static bool IsImportable(string path) =>
		Path.GetExtension(path).ToLowerInvariant() is ".props" or ".targets";
}
