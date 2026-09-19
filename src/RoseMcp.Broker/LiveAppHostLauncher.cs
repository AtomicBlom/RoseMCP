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

		var inRepository = searchRepository ? RepositoryBuildOutput.Find(HostName, executableName, installRoot, rid) : null;
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
}
