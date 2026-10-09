namespace RoseMcp.TestSupport;

/// <summary>
/// A directory with no project or solution anywhere above it.
/// <para>
/// Harder than it sounds. Resolution walks up to the drive root, so "somewhere empty" has to mean
/// every ancestor is empty too, and a developer's %TEMP% collects stray .csproj files from other
/// tools -- four of them were enough to make two tests fail on one machine and pass everywhere
/// else, by finding one and correctly falling back to it. Nowhere on a real disk can be promised
/// clean, so this points at a disk that is not there: resolution handles a path that does not
/// exist by design, and an absent drive has an ancestry of exactly one empty directory.
/// </para>
/// <para>
/// A drive letter is a root only on Windows. Anywhere else <c>Z:\rosemcp-nowhere</c> is a relative
/// file name, and tests use this as the directory a caller stands in: a relative path argument is
/// measured only from a fully qualified directory, so with that shape every tool throws
/// <see cref="ArgumentException"/> while building its arguments, before it reaches the behaviour
/// the test asks about. With no drives to be absent, the stand-in off Windows is a directory under
/// the filesystem root, whose only ancestor is the root itself -- where no build tool leaves a
/// project, and where nothing without root privileges can create one.
/// </para>
/// </summary>
public static class NowhereDirectory
{
	/// <summary>
	/// A fully qualified path to a directory that does not exist: under the first drive letter this
	/// machine does not have on Windows, and directly under the filesystem root elsewhere.
	/// </summary>
	public static string Path() => OperatingSystem.IsWindows() ? UnderAnAbsentDrive() : UnderTheRoot();

	private static string UnderAnAbsentDrive()
	{
		var used = DriveInfo.GetDrives()
			.Select(drive => char.ToUpperInvariant(drive.Name[0]))
			.ToHashSet();

		for (var letter = 'Z'; letter >= 'D'; letter--)
		{
			if (!used.Contains(letter)) return letter + @":\rosemcp-nowhere";
		}

		throw new InvalidOperationException(
			"Every drive letter from D to Z is in use, so there is no absent drive to point at.");
	}

	/// <summary>
	/// Refuses rather than returns a directory somebody made, because a project or solution in it is
	/// one every test relying on finding none would find.
	/// </summary>
	private static string UnderTheRoot()
	{
		const string Nowhere = "/rosemcp-nowhere";

		if (Directory.Exists(Nowhere))
		{
			throw new InvalidOperationException(
				$"{Nowhere} exists, so it is not nowhere. Remove it: tests use it as a directory with no project or solution in or above it.");
		}

		return Nowhere;
	}
}
