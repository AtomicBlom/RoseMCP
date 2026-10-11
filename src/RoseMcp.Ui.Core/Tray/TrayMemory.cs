namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// What the tray remembers between runs about what it has already said: which release it announced,
/// and which build ran last, so a restart can say what it moved from.
/// <para>
/// Its own file rather than a field of the shared settings, because none of it is a choice somebody
/// made and only the tray writes it. The settings file is read and written whole by every window, so a
/// field the tray rewrote there on every start and every release would be a field each of them could
/// overwrite with the copy it read a minute earlier.
/// </para>
/// </summary>
public sealed record TrayMemory
{
	/// <summary>The newest release the tray has announced, so each one is announced once.</summary>
	public string? NotifiedRelease { get; init; }

	/// <summary>The commit the tray that ran last was built from, where it named one.</summary>
	public string? LastCommit { get; init; }

	/// <summary>The version the tray that ran last was.</summary>
	public string? LastVersion { get; init; }

	/// <summary>
	/// Whether <paramref name="latest"/> is a release the person has not been told about. Versions are
	/// compared without a leading <c>v</c> and ignoring case, since a tag and a version string spell the
	/// same release both ways.
	/// </summary>
	public bool IsNewRelease(bool available, string? latest) =>
		available && latest is { Length: > 0 } && !string.Equals(Bare(latest), Bare(NotifiedRelease), StringComparison.OrdinalIgnoreCase);

	private static string? Bare(string? version) => version?.Trim().TrimStart('v', 'V');
}
