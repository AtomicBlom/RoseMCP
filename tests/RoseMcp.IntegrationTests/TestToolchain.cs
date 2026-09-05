using System.Diagnostics;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Running build tools and finding this repository from inside the test binary.
/// <para>
/// Its own class rather than private helpers on one test class, because <see cref="UwpProbeApp"/>
/// needs the same four things and duplicating them is how two copies of "which configuration am I"
/// come to disagree. Consumers bring the members into scope with
/// <c>using static RoseMcp.IntegrationTests.TestToolchain;</c>, so a call site reads the same as it
/// did when these were private.
/// </para>
/// </summary>
internal static class TestToolchain
{
	// The x64 host and target are built on demand for the architecture-shim test, since a normal build
	// produces only the broker's own RID. On an x64 machine this is a same-arch build; on ARM it is the
	// emulated-x64 case classic UWP needs.
	//
	// Built once per test run, never merely "found". Skipping the build when the exe already exists is
	// the obvious optimisation and it is wrong: the win-x64 output is a separate RID build that a normal
	// `dotnet build` of the solution does not touch, so an existing exe is routinely one source change
	// out of date -- and the test then exercises yesterday's host and reports a failure that is not
	// there. MSBuild is incremental, so paying for the check once a run costs almost nothing.
	private static readonly Dictionary<string, string> X64Builds = [];

	internal static void EnsureX64HostBuilt() => EnsureX64Build("src", "RoseMcp.LiveApp", "net10.0-windows", "RoseMcp.LiveApp.exe");

	internal static string EnsureX64ProbeTargetBuilt() => EnsureX64Build("tests", "DebugProbeTarget", "net10.0", "DebugProbeTarget.exe");

	internal static string EnsureX64Build(string area, string project, string targetFramework, string exeName)
	{
		var root = RepositoryRoot();
		var configuration = Configuration();
		var exe = Path.Combine(root, area, project, "bin", configuration, targetFramework, "win-x64", exeName);

		lock (X64Builds)
		{
			if (X64Builds.TryGetValue(exe, out var built)) return built;

			var csproj = Path.Combine(root, area, project, $"{project}.csproj");
			RunDotnet($"build \"{csproj}\" -r win-x64 -c {configuration} --nologo");

			if (!File.Exists(exe)) throw new FileNotFoundException($"The win-x64 build did not produce {exeName}.", exe);
			X64Builds[exe] = exe;
			return exe;
		}
	}

	internal static void RunDotnet(string arguments)
	{
		var (exitCode, output) = RunProcess("dotnet", arguments);
		if (exitCode != 0) throw new InvalidOperationException($"dotnet {arguments} failed:{Environment.NewLine}{output}");
	}

	internal static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
	{
		var start = new ProcessStartInfo(fileName, arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();
		return (process.ExitCode, output);
	}

	internal static string RepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");
	}

	internal static string Configuration()
		=> AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
			? "Release"
			: "Debug";
}
