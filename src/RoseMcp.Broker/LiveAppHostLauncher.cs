using System.Runtime.InteropServices;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Locates the live-app host executable for a target architecture. Unlike the worker, which always
/// runs as the broker's architecture, the host must match the target: an ARM64 host for modern .NET,
/// an x64 host for a classic UWP app running under emulation.
/// </summary>
public static class LiveAppHostLauncher
{
	private const string HostName = "RoseMcp.LiveApp";

	/// <summary>
	/// The hosts file name on this operating system. Public because a test that stages one has to
	/// stage the name this looks for: hard-coding the Windows name there passes on Windows and fails
	/// on Linux for a reason that has nothing to do with what it is testing.
	/// </summary>
	public static string ExecutableName =>
		HostName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);

	public static string ResolveHostPath(
		TargetArchitecture architecture,
		BrokerOptions options,
		string? baseDirectory = null,
		bool searchRepository = true)
	{
		var rid = RuntimeIdentifierFor(architecture);
		var executableName = ExecutableName;

		var configured = Environment.GetEnvironmentVariable("ROSEMCP_LIVEAPP_HOST");
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

		// A published layout places per-RID hosts beside the broker under a well-known folder. Two
		// places count as "beside", because two things host the broker: the server publishes flat into
		// the install root, and the tray goes in a tray/ subfolder deliberately -- WinUI drags in
		// enough that mixing it with the server risks one overwriting the other's shared assemblies.
		// So the hosts are published once, into the root, and the tray finds them one level up rather
		// than every deploy shipping a second copy of both architectures.
		var installRoot = baseDirectory ?? AppContext.BaseDirectory;

		foreach (var root in new[] { installRoot, Path.Combine(installRoot, "..") })
		{
			var alongside = Path.Combine(root, "live-app", rid, executableName);
			if (File.Exists(alongside)) return Path.GetFullPath(alongside);
		}

		var inRepository = searchRepository ? FindInRepository(executableName, rid, installRoot) : null;
		if (inRepository is not null) return inRepository;

		throw new FileNotFoundException(
			$"Could not find a {rid} {executableName}. Build RoseMcp.LiveApp for {rid}, publish it under "
				+ "'live-app/<rid>' beside the broker, or set ROSEMCP_LIVEAPP_HOST.");
	}

	private static string RuntimeIdentifierFor(TargetArchitecture architecture) => architecture switch
	{
		TargetArchitecture.Arm64 => "win-arm64",
		TargetArchitecture.X64 => "win-x64",
		TargetArchitecture.X86 => "win-x86",

		// Unknown: fall back to the broker's own architecture, which is right for a same-arch target.
		_ => RuntimeInformation.RuntimeIdentifier,
	};

	/// <summary>
	/// Development fallback: find the host in its own build output, narrowed to builds that could
	/// serve the wanted architecture and then to the one most likely to be current.
	/// </summary>
	/// <param name="executableName">The host's file name, with the platform's extension.</param>
	/// <param name="rid">The runtime identifier the target needs a host for.</param>
	/// <param name="baseDirectory">
	/// Where the broker is running from. It decides two things: which repository this is, by walking
	/// up to the solution file, and which configuration was built, by the folder it sits in.
	/// </param>
	private static string? FindInRepository(string executableName, string rid, string baseDirectory)
	{
		var directory = new DirectoryInfo(baseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		if (directory is null) return null;

		var hostRoot = Path.Combine(directory.FullName, "src", HostName, "bin");
		if (!Directory.Exists(hostRoot)) return null;

		var candidates = Directory.EnumerateFiles(hostRoot, executableName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.ToList();

		// Narrowed to the broker's own configuration before anything else, because `deploy.ps1`
		// publishes Release per RID and leaves those artefacts in the repository's bin. Preferring a
		// RID match on its own then let a Release host shadow a Debug build that was twenty minutes
		// newer, so a Debug test run silently exercised a stale binary and a change under test was
		// simply not there. An artefact existing is not the same as it being the current one.
		var configuration = ConfigurationOf(baseDirectory);
		if (configuration is not null)
		{
			var matching = candidates
				.Where(path => path.Contains($"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (matching.Count > 0) candidates = matching;
		}

		// A build whose path carries the wanted RID is unambiguous about its architecture.
		var ridMatch = candidates.FirstOrDefault(path => path.Contains(rid, StringComparison.OrdinalIgnoreCase));

		// A RID-less build is the broker's own architecture, so it is a candidate only when that is
		// the architecture wanted -- and it must not be some other RID's output, or a foreign host
		// would be handed back (an x64 build sitting in bin beside an arm64 one, say).
		var plain = rid == RuntimeInformation.RuntimeIdentifier
			? candidates.FirstOrDefault(path => !CarriesAnyRid(path))
			: null;

		if (ridMatch is null) return plain;
		if (plain is null) return ridMatch;

		// Both are the right configuration and the right architecture, so neither is more correct
		// than the other and the newer one is what somebody just built. The same trap one layer
		// down: an ordinary build produces no per-RID output, so preferring the RID match outright
		// left an earlier per-RID one in bin to answer for it. The process under test was then from
		// before the change, and nothing about the run said so -- the tools it was missing came back
		// as unknown tools, which reads like the tools being wrong rather than the binary being old.
		return File.GetLastWriteTimeUtc(plain) > File.GetLastWriteTimeUtc(ridMatch) ? plain : ridMatch;
	}

	/// <summary>
	/// Which build configuration a directory belongs to, or null when it is not a build output at all
	/// (a published layout, say, where the question does not arise).
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
