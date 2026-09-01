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
/// </summary>
public static class NowhereDirectory
{
	/// <summary>A path under the first drive letter this machine does not have.</summary>
	public static string Path()
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
}
