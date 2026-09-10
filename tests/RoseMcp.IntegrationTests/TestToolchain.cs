using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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

	/// <summary>
	/// Registers a loose AppX layout and returns its package family name, installing any framework it
	/// depends on that this machine does not have.
	/// </summary>
	/// <param name="manifest">The layout's AppxManifest.xml.</param>
	/// <param name="packageName">The package identity name, to read the family name back by.</param>
	/// <param name="failure">Why it could not be registered, when it could not.</param>
	/// <remarks>
	/// A framework dependency is the one registration failure that is neither a bug nor a limit of the
	/// machine: it is a package sitting unregistered in the Windows SDK, and installing it is a single
	/// call. Leaving it to the person means an acceptance test skips for as long as nobody reads an
	/// event log -- <c>Microsoft.VCLibs.140.00.Debug</c> was installed for x64 only on an ARM64 machine
	/// here, and the two modern-UWP tests skipped every run until somebody looked.
	/// <para>
	/// Which framework is asked of Windows rather than worked out from the manifest. The deployment
	/// error names the package, the architecture and the minimum version it wants, which is more than a
	/// manifest read would give and cannot drift from what the deployment engine actually enforces.
	/// </para>
	/// <para>
	/// One retry, and then it says what it could not do. Installing a framework twice is harmless but a
	/// loop that keeps trying hides a failure that is not about frameworks at all.
	/// </para>
	/// </remarks>
	internal static string? RegisterAppxLayout(string manifest, string packageName, out string? failure)
	{
		failure = null;

		if (!File.Exists(manifest))
		{
			failure = $"there is no AppxManifest.xml at {manifest}";
			return null;
		}

		var registered = TryRegister(manifest, packageName, out var reported);
		if (registered is not null) return registered;

		if (MissingFramework(reported) is not { } wanted)
		{
			failure = reported;
			return null;
		}

		if (InstallFramework(wanted, out var installFailure) is false)
		{
			failure = $"{reported} Installing {wanted.Name} for {wanted.Architecture} failed: {installFailure}";
			return null;
		}

		registered = TryRegister(manifest, packageName, out reported);
		if (registered is not null) return registered;

		failure = $"{reported} {wanted.Name} for {wanted.Architecture} was installed first, so this is not the missing framework.";
		return null;
	}

	/// <summary>
	/// One attempt at registering the layout: the package family name, or null with whatever the
	/// deployment engine said.
	/// </summary>
	private static string? TryRegister(string manifest, string packageName, out string reported)
	{
		var script =
			$"try {{ Add-AppxPackage -Register '{manifest}' -ErrorAction Stop }} catch {{ Write-Output ('ERROR: ' + $_.Exception.Message); exit 0 }}; "
				+ $"$p = Get-AppxPackage '{packageName}'; if ($p) {{ Write-Output ('PFN: ' + $p.PackageFamilyName) }}";

		var (_, output) = RunProcess("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");

		var lines = output.Split('\n').Select(line => line.Trim()).ToArray();
		var pfn = lines.FirstOrDefault(line => line.StartsWith("PFN: ", StringComparison.Ordinal));

		if (pfn is not null)
		{
			reported = string.Empty;
			return pfn["PFN: ".Length..].Trim();
		}

		reported = lines.FirstOrDefault(line => line.StartsWith("ERROR: ", StringComparison.Ordinal))
			?? "Add-AppxPackage reported nothing and the package is not registered.";

		return null;
	}

	/// <summary>
	/// The framework a deployment error is asking for, or null where it is asking for something else.
	/// </summary>
	/// <remarks>
	/// Read out of the message Windows writes for ERROR_INSTALL_RESOLVE_DEPENDENCY_FAILED, which names
	/// the package and the architectures that would satisfy it:
	/// <code>
	/// Provide the framework "Microsoft.VCLibs.140.00.Debug" published by "CN=Microsoft Corporation,
	/// ..." with neutral or ARM64 processor architecture and minimum version 14.0.33519.0, along with
	/// this package to install.
	/// </code>
	/// Parsing prose is not something to do lightly, and it earns it here: the alternative is a list of
	/// framework names kept in the fixture by hand, which is a guess about what the deployment engine
	/// wants rather than a reading of what it asked for, and it goes stale the first time a probe gains
	/// a dependency.
	/// </remarks>
	private static FrameworkDependency? MissingFramework(string reported)
	{
		var name = Regex.Match(reported, @"Provide the framework ""([^""]+)""");
		if (!name.Success) return null;

		var architecture = Regex.Match(reported, @"with neutral or (\w+) processor architecture");

		return new FrameworkDependency(
			name.Groups[1].Value,
			architecture.Success ? architecture.Groups[1].Value : RuntimeInformation.ProcessArchitecture.ToString());
	}

	/// <summary>A framework package a layout needs, as the deployment engine described it.</summary>
	private readonly record struct FrameworkDependency(string Name, string Architecture);

	/// <summary>
	/// Installs a framework package from the Windows SDK, or says why it could not.
	/// </summary>
	/// <remarks>
	/// The SDK ships these under <c>ExtensionSDKs</c>, one folder per architecture, and the file names
	/// do not match the package names -- <c>Microsoft.VCLibs.140.00.Debug</c> is
	/// <c>Microsoft.VCLibs.arm64.Debug.14.00.appx</c> on disk. So the search matches on the parts that
	/// do carry over: the family (the name up to its version), the architecture folder, and whether the
	/// wanted package is the Debug flavour, which is a different package identity rather than a
	/// different build of one.
	/// </remarks>
	private static bool InstallFramework(FrameworkDependency wanted, out string failure)
	{
		failure = string.Empty;

		var roots = new[]
		{
			Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
				"Microsoft SDKs", "Windows Kits", "10", "ExtensionSDKs"),
			Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
				"Microsoft SDKs", "Windows Kits", "10", "ExtensionSDKs"),
		};

		// "Microsoft.VCLibs.140.00.Debug" -> "Microsoft.VCLibs", which is the ExtensionSDKs folder.
		var family = string.Join('.', wanted.Name.Split('.').Take(2));
		var debug = wanted.Name.Contains(".Debug", StringComparison.OrdinalIgnoreCase);

		var candidate = roots
			.Where(Directory.Exists)
			.SelectMany(root => SafeFiles(Path.Combine(root, family), "*.appx"))
			.Where(path => path.Contains(wanted.Architecture, StringComparison.OrdinalIgnoreCase))
			.Where(path => path.Contains(".Debug", StringComparison.OrdinalIgnoreCase) == debug)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();

		if (candidate is null)
		{
			failure = $"no {wanted.Name} package for {wanted.Architecture} was found under the Windows SDK's "
				+ $"ExtensionSDKs\\{family}. Install it by hand, or install the Windows SDK component that ships it.";
			return false;
		}

		var (_, output) = RunProcess(
			"powershell",
			$"-NoProfile -NonInteractive -Command \"try {{ Add-AppxPackage -Path '{candidate}' -ErrorAction Stop }} "
				+ "catch { Write-Output ('ERROR: ' + $_.Exception.Message) }\"");

		var error = output.Split('\n').Select(line => line.Trim())
			.FirstOrDefault(line => line.StartsWith("ERROR: ", StringComparison.Ordinal));

		if (error is null) return true;

		failure = $"{error} (from {candidate})";
		return false;
	}

	/// <summary>
	/// Every file under a directory that may not be there, because a machine without a given Windows
	/// SDK component simply has no folder for it and that is not an error worth throwing over.
	/// </summary>
	private static IEnumerable<string> SafeFiles(string directory, string pattern)
	{
		if (!Directory.Exists(directory)) return [];

		try
		{
			return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
