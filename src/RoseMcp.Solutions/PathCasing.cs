namespace RoseMcp.Solutions;

/// <summary>
/// Whether two paths that differ only in case name the same file, which is a question about the
/// platform rather than about the paths.
/// <para>
/// It exists because the answer was assumed to be yes in four places. Every path-keyed dictionary
/// here used <see cref="StringComparer.OrdinalIgnoreCase"/>, and both
/// <see cref="WorkspaceKey.For"/> and the log file naming lowercased a path before hashing it. All
/// four are right on Windows, where one solution is reached through many casings and all of them are
/// the same solution -- and all four are wrong on Linux, where <c>/repo/A.slnx</c> and
/// <c>/repo/a.slnx</c> are two files.
/// </para>
/// <para>
/// The consequences are not cosmetic. Two distinct solutions folded together share one worker, so a
/// question about one is answered from the other's compilation; they share one workspace key, which
/// is the name a caller quotes back; and they share one log file name, where Serilog's exclusive
/// lock means the loser logs nothing at all. Each of those is the silent-and-plausible shape this
/// repository refuses everywhere else, and each was reachable only on the platform that had never
/// been run.
/// </para>
/// <para>
/// Windows is the only case-insensitive platform this ships for. macOS is deliberately not
/// considered: its default is case-insensitive and configurable, so guessing would be worse than
/// the honest note that this is the one place to revisit if it is ever targeted.
/// </para>
/// </summary>
public static class PathCasing
{
	/// <summary>Whether the filesystem this is running on treats path case as insignificant.</summary>
	public static bool IsInsensitive { get; } = OperatingSystem.IsWindows();

	/// <summary>The comparer for keying anything by path.</summary>
	public static StringComparer Comparer { get; } =
		IsInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>
	/// A path reduced to the form every path naming the same file shares, for hashing. Not for
	/// display, and not for opening anything: on Windows the result is lowercased.
	/// </summary>
	public static string Fold(string path) => IsInsensitive ? path.ToLowerInvariant() : path;
}
