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

	internal static string EnsureX64Build(string area, string project, string targetFramework, string exeName) =>
		EnsureRidBuild("win-x64", area, project, targetFramework, exeName);

	internal static void EnsureX86HostBuilt() =>
		EnsureRidBuild("win-x86", "src", "RoseMcp.LiveApp", "net10.0-windows", "RoseMcp.LiveApp.exe");

	internal static string EnsureX86ProbeTargetBuilt() =>
		EnsureRidBuild("win-x86", "tests", "DebugProbeTarget", "net10.0", "DebugProbeTarget.exe");

	/// <summary>
	/// One project built for one runtime identifier, once a run.
	/// <para>
	/// The runtime identifier is a parameter because x64 is no longer the only architecture worth
	/// proving. An install ships an x86 host wherever it ships an x64 one, and x86 is not a legacy
	/// case here -- it is the default platform of the modern UWP template, so it is what an ordinary
	/// new app is built and registered as. Until something builds an x86 target and attaches to it,
	/// nothing says that host works.
	/// </para>
	/// </summary>
	internal static string EnsureRidBuild(
		string rid,
		string area,
		string project,
		string targetFramework,
		string exeName)
	{
		var root = RepositoryRoot();
		var configuration = Configuration();
		var exe = Path.Combine(root, area, project, "bin", configuration, targetFramework, rid, exeName);

		lock (X64Builds)
		{
			if (X64Builds.TryGetValue(exe, out var built)) return built;

			var csproj = Path.Combine(root, area, project, $"{project}.csproj");
			RunDotnet($"build \"{csproj}\" -r {rid} -c {configuration} --nologo");

			if (!File.Exists(exe)) throw new FileNotFoundException($"The {rid} build did not produce {exeName}.", exe);
			X64Builds[exe] = exe;
			return exe;
		}
	}

	/// <summary>
	/// The native XAML providers built so far this run, by project and platform, so each is built once
	/// however many fixtures ask for it.
	/// </summary>
	private static readonly Dictionary<string, bool> XamlProviders = [];

	private static readonly Lock XamlProviderGate = new();

	/// <summary>
	/// Builds one native XAML provider with its <c>build.ps1</c>, once a run. False where the machine
	/// simply cannot -- build.ps1's exit 3, meaning no MSVC toolset or no Windows SDK -- so the calling
	/// test skips rather than going red on an environment limit.
	/// <para>
	/// Only exit 3 is skippable, and that distinction is load-bearing. This once returned false for any
	/// non-zero exit and the caller skipped saying "no C++ toolset", so a compile error in the provider
	/// silently skipped the XAML tests and left the suite green. A capability quietly not being tested
	/// is worse than a red build and looks identical to a machine that cannot build it. build.ps1
	/// already separates the two: it exits 3 from its own Fail for a missing toolset or SDK, and
	/// anything else is a real failure.
	/// </para>
	/// <para>
	/// Here rather than on a fixture, and that is the whole point of it. A provider belongs to the
	/// repository, not to whichever fixture happened to want it first: <see cref="UwpProbeApp"/> and
	/// <see cref="UwpModernProbeApp"/> inject the <em>same</em> UWP tap, because it is the same XAML
	/// framework, so with a probe each they ran two <c>cl.exe</c> processes over one output directory
	/// and the build died on <c>C1041: cannot open program database ... vc140.pdb</c>. Two locks over
	/// one shared thing is not two locks, it is none -- the rule this repository keeps applying one
	/// layer too high. The app gates are correctly separate, since those are separate processes; the
	/// build they share is what needed a gate of its own.
	/// </para>
	/// <para>
	/// The lock is held across the build rather than only around the memo, because serialising the
	/// answer while racing the work is exactly the bug. A second caller waits out the twenty-odd
	/// seconds once and then reads the result.
	/// </para>
	/// </summary>
	/// <param name="projectName">The provider's project directory under <c>src</c>.</param>
	/// <param name="platform">The MSVC platform to build for; it must match the target process.</param>
	internal static bool EnsureXamlProviderBuilt(string projectName, string platform)
	{
		var key = $"{projectName}|{platform}";

		lock (XamlProviderGate)
		{
			if (XamlProviders.TryGetValue(key, out var built)) return built;

			var script = Path.Combine(RepositoryRoot(), "src", projectName, "build.ps1");
			if (!File.Exists(script)) return XamlProviders[key] = false;

			var (exitCode, output) = RunProcess(
				"powershell",
				$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Platform {platform} -Configuration Debug");

			// 3 is build.ps1's Fail: no MSVC toolset, or no Windows SDK. The only skippable outcome.
			if (exitCode == 3) return XamlProviders[key] = false;

			if (exitCode != 0)
			{
				throw new InvalidOperationException(
					$"Building {projectName} failed (exit {exitCode}):{Environment.NewLine}{output}");
			}

			var dll = Path.Combine(RepositoryRoot(), "src", projectName, "bin", platform, "Debug", $"{projectName}.dll");
			if (!File.Exists(dll))
			{
				throw new InvalidOperationException($"The {projectName} build reported success but produced no {dll}.");
			}

			return XamlProviders[key] = true;
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
