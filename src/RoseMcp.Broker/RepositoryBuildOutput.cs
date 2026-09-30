using System.Runtime.InteropServices;

namespace RoseMcp.Broker;

/// <summary>
/// Finds a sibling project's build output in the repository, for a broker running from source.
/// <para>
/// The failure this exists to prevent is the worst shape available: an older binary answers, every
/// call that already existed still works, and only the calls a change added come back as unknown
/// tools -- which reads as the tools being wrong rather than as the process being from before the
/// change. Nothing in the run says the binary was stale.
/// </para>
/// <para>
/// So the policy is configuration first, architecture second, recency last, and it lives here
/// because all three launchers need it and two of them had it. The worker's did not, and the
/// worker is the process every Roslyn test drives.
/// </para>
/// </summary>
public static class RepositoryBuildOutput
{
	/// <summary>
	/// The newest matching executable under <c>src/<paramref name="projectName"/>/bin</c>, or null
	/// where the repository or the output is not there.
	/// </summary>
	/// <param name="projectName">The folder under <c>src</c> that builds it.</param>
	/// <param name="executableName">The file to look for, extension included.</param>
	/// <param name="from">
	/// Where the parent is running from. Both the repository walk and the configuration are read off
	/// it, so a Debug broker asks for a Debug child.
	/// </param>
	/// <param name="rid">
	/// The runtime identifier the child has to be, where that is a fact about correctness rather
	/// than about freshness. Null asks only for the newest right-configuration build, which is what
	/// a process that runs in the parent's own architecture wants.
	/// </param>
	public static string? Find(string projectName, string executableName, string from, string? rid = null)
	{
		var directory = new DirectoryInfo(from);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		if (directory is null) return null;

		var root = Path.Combine(directory.FullName, "src", projectName, "bin");
		if (!Directory.Exists(root)) return null;

		var candidates = Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.ToList();

		// Narrowed to the parent's own configuration before anything else, because `deploy.ps1`
		// publishes Release per RID and leaves those artefacts in the repository's bin. Preferring a
		// RID match on its own then lets a Release build shadow a Debug one that is twenty minutes
		// newer, so a Debug test run exercises a stale binary and a change under test is simply not
		// there. An artefact existing is not the same as it being the current one.
		var configuration = ConfigurationOf(from);
		if (configuration is not null)
		{
			var matching = candidates
				.Where(path => path.Contains(
					$"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}",
					StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (matching.Count > 0) candidates = matching;
		}

		if (rid is null) return candidates.FirstOrDefault();

		// A build whose path carries the wanted runtime identifier is unambiguous about its
		// architecture.
		var ridMatch = candidates.FirstOrDefault(path => path.Contains(rid, StringComparison.OrdinalIgnoreCase));

		// A RID-less build is the parent's own architecture, so it is a candidate only when that is
		// the architecture wanted -- and it must not be some other RID's output, or a foreign binary
		// would be handed back (an x64 build sitting in bin beside an arm64 one, say).
		var plain = rid == RuntimeInformation.RuntimeIdentifier
			? candidates.FirstOrDefault(path => !CarriesAnyRid(path))
			: null;

		if (ridMatch is null) return plain;
		if (plain is null) return ridMatch;

		// Both are the right configuration and the right architecture, so neither is more correct
		// than the other and the newer one is what somebody just built. The same trap one layer
		// down: an ordinary build produces no per-RID output, so preferring the RID match outright
		// leaves an earlier per-RID one in bin to answer for it.
		return File.GetLastWriteTimeUtc(plain) > File.GetLastWriteTimeUtc(ridMatch) ? plain : ridMatch;
	}

	/// <summary>
	/// Which build configuration a directory belongs to, or null where it is not a build output at
	/// all (a published layout, say, where the question does not arise).
	/// </summary>
	private static string? ConfigurationOf(string directory)
	{
		var separator = Path.DirectorySeparatorChar;

		foreach (var configuration in new[] { "Debug", "Release" })
		{
			if (directory.Contains($"{separator}{configuration}{separator}", StringComparison.OrdinalIgnoreCase))
			{
				return configuration;
			}
		}

		return null;
	}

	private static bool CarriesAnyRid(string path) =>
		path.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("win-arm64", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("win-x86", StringComparison.OrdinalIgnoreCase);
}
