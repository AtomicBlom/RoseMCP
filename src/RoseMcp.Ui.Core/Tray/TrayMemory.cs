namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// What the tray remembers between runs about what it has already said: which release it announced, and
/// which build each install ran last, so a restart can say what it moved from.
/// <para>
/// Its own file rather than a field of the shared settings, because none of it is a choice somebody made and
/// only the tray writes it. The settings file is read and written whole by every window, so a field the tray
/// rewrote there on every start would be one each of them could overwrite with the copy it read earlier.
/// </para>
/// <para>
/// The release is one value for the machine, since a person told about it once has been told. The last build
/// is per install, keyed by the executable's path, because an installed tray and one run from a source tree
/// share this file and each restart is about its own build.
/// </para>
/// </summary>
public sealed record TrayMemory
{
	/// <summary>How many installs' last builds are kept; the longest unseen go first.</summary>
	public const int InstallsKept = 32;

	/// <summary>The newest release a tray on this machine has announced, so each one is announced once.</summary>
	public string? NotifiedRelease { get; init; }

	/// <summary>The build each install ran last, keyed by the path of its executable.</summary>
	public IReadOnlyDictionary<string, LastBuild> LastBuilds { get; init; } = new Dictionary<string, LastBuild>();

	/// <summary>
	/// The commit <paramref name="install"/> ran last, where it named one. Paths are compared ignoring case,
	/// as Windows compares them.
	/// </summary>
	public string? LastCommitOf(string install) =>
		LastBuilds.FirstOrDefault(entry => string.Equals(entry.Key, install, StringComparison.OrdinalIgnoreCase)).Value?.Commit;

	/// <summary>This memory with <paramref name="install"/>'s last build replaced, and the others kept to <see cref="InstallsKept"/>.</summary>
	public TrayMemory WithLastBuild(string install, string? commit, DateTimeOffset ranUtc)
	{
		var others = LastBuilds
			.Where(entry => !string.Equals(entry.Key, install, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(entry => entry.Value.RanUtc)
			.Take(InstallsKept - 1);

		var builds = others.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
		if (commit is { Length: > 0 }) builds[install] = new LastBuild(commit, ranUtc);

		return this with { LastBuilds = builds };
	}

	/// <summary>
	/// Whether <paramref name="latest"/> is a release the person has not been told about. Versions are compared
	/// without a leading <c>v</c> and ignoring case, since a tag and a version string spell the same release
	/// both ways.
	/// </summary>
	public bool IsNewRelease(bool available, string? latest) =>
		available && latest is { Length: > 0 } && !string.Equals(Bare(latest), Bare(NotifiedRelease), StringComparison.OrdinalIgnoreCase);

	private static string? Bare(string? version) => version?.Trim().TrimStart('v', 'V');
}
