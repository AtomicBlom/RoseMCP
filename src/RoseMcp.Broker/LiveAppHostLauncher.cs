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

	public static string ResolveHostPath(TargetArchitecture architecture, BrokerOptions options)
	{
		var rid = RuntimeIdentifierFor(architecture);
		var executableName = HostName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);

		var configured = Environment.GetEnvironmentVariable("ROSEMCP_LIVEAPP_HOST");
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

		// A published layout places per-RID hosts beside the broker under a well-known folder.
		var alongside = Path.Combine(AppContext.BaseDirectory, "live-app", rid, executableName);
		if (File.Exists(alongside)) return alongside;

		var inRepository = FindInRepository(executableName, rid);
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
	/// Development fallback: find the host in its own build output, preferring a build whose path
	/// carries the wanted RID, and accepting a plain (RID-less) build only when it matches the
	/// broker's architecture.
	/// </summary>
	private static string? FindInRepository(string executableName, string rid)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
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

		var ridMatch = candidates.FirstOrDefault(path => path.Contains(rid, StringComparison.OrdinalIgnoreCase));
		if (ridMatch is not null) return ridMatch;

		// A plain build output is the broker's architecture; use it only when that is what was asked.
		return rid == RuntimeInformation.RuntimeIdentifier ? candidates.FirstOrDefault() : null;
	}
}
