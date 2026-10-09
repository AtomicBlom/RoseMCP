using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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

	/// <summary>
	/// What <c>MSBuildLocator</c> sets in whatever process registers it, and so what a spawned build
	/// must not be handed. Named here rather than at the removal, because the set is a fact about
	/// MSBuildLocator rather than about any one tool being run.
	/// </summary>
	private static readonly string[] MSBuildEnvironment =
	[
		"MSBUILD_EXE_PATH",
		"MSBuildExtensionsPath",
		"MSBuildSDKsPath",
	];

	/// <summary>
	/// Runs a tool and returns its exit code and everything it wrote, with this process's MSBuild
	/// environment kept out of the child.
	/// <para>
	/// <c>MSBuildLocator.RegisterInstance</c> points a process at one MSBuild by setting
	/// <c>MSBUILD_EXE_PATH</c>, <c>MSBuildExtensionsPath</c> and <c>MSBuildSDKsPath</c> in its own
	/// environment, and the tests that drive Roslyn in process rather than through a worker make that
	/// happen inside the test runner. A child inherits them, so a spawned Visual Studio MSBuild
	/// resolves <c>$(MSBuildExtensionsPath)</c> to the dotnet SDK: the classic UWP project's
	/// <c>Microsoft\WindowsXaml\v18.0</c> import is looked for under the SDK, is not there, and the
	/// build fails naming a missing targets file on a machine that has it. The error reads like a
	/// broken project and the cause is that the build was pointed at the wrong root.
	/// </para>
	/// <para>
	/// Scrubbed for every tool rather than for the build that noticed, because wanting the toolchain
	/// it finds for itself is the whole reason any of this is spawned instead of being done in
	/// process. Removing a variable that was never set costs nothing, so there is no condition on it.
	/// </para>
	/// </summary>
	internal static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
	{
		var start = new ProcessStartInfo(fileName, arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var inherited in MSBuildEnvironment) start.Environment.Remove(inherited);

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
	/// Registers a loose AppX layout and returns its package family name, installing the frameworks it
	/// depends on where this machine does not have them.
	/// </summary>
	/// <param name="manifest">The layout's AppxManifest.xml.</param>
	/// <param name="packageName">The package identity name, to read the family name back by.</param>
	/// <param name="recipe">
	/// The build's <c>.build.appxrecipe</c>, whose resolved SDK references name the framework packages
	/// the build linked against and where each one's <c>.appx</c> sits; null where the build writes
	/// none.
	/// </param>
	/// <param name="failure">Why it could not be registered, when it could not.</param>
	/// <remarks>
	/// A framework dependency is the one registration failure that is neither a bug nor a limit of the
	/// machine: it is a package sitting unregistered beside the build, and installing it is a single
	/// call. Leaving it to the person means an acceptance test skips for as long as nobody reads an
	/// event log -- <c>Microsoft.VCLibs.140.00.Debug</c> was installed for x64 only on an ARM64 machine
	/// here, and the two modern-UWP tests skipped every run until somebody looked. A machine Visual
	/// Studio has never deployed a UWP app from, a hosted CI runner among them, need have none of the
	/// three debug frameworks the classic probe depends on.
	/// <para>
	/// Whether a framework is missing, and for which architecture, is asked of Windows rather than
	/// worked out from the manifest: the deployment error names the package, the architecture and the
	/// minimum version it wants, and that cannot drift from what the deployment engine enforces. Where
	/// to get it is asked of the build. The recipe lists the same framework packages Visual Studio's
	/// deploy installs, for every architecture the build could target, so everything the recipe holds
	/// for the architecture Windows named is installed at once -- a classic UWP debug build needs three,
	/// and Windows names only the first it finds missing. A framework the recipe does not hold is looked
	/// for in the Windows SDK's <c>ExtensionSDKs</c>, which is where the VCLibs ones live.
	/// </para>
	/// <para>
	/// Installed as packages of their own, then the layout registered, because that is the only order
	/// the deployment engine accepts: <c>Add-AppxPackage -Register -DependencyPath</c> takes the
	/// dependencies as further loose layouts to register and refuses an <c>.appx</c> with "the manifest
	/// is not in the package root".
	/// </para>
	/// <para>
	/// One retry, and then it says what it could not do. Installing a framework twice is harmless but a
	/// loop that keeps trying hides a failure that is not about frameworks at all.
	/// </para>
	/// <para>
	/// Under one gate for every fixture, because the UWP probes register in parallel and share their
	/// frameworks: two fixtures installing <c>Microsoft.VCLibs.140.00.Debug</c> at once would race a
	/// machine-wide deployment for no gain, when the second only needs to find it done.
	/// </para>
	/// </remarks>
	internal static string? RegisterAppxLayout(string manifest, string packageName, string? recipe, out string? failure)
	{
		failure = null;

		if (!File.Exists(manifest))
		{
			failure = $"there is no AppxManifest.xml at {manifest}";
			return null;
		}

		lock (RegistrationGate)
		{
			var registered = TryRegister(manifest, packageName, out var reported);
			if (registered is not null) return registered;

			if (MissingFramework(reported) is not { } wanted)
			{
				failure = reported;
				return null;
			}

			var installed = InstallFrameworks(wanted, recipe, out var installFailure);
			if (installed is null)
			{
				failure = $"{reported} Installing {wanted.Name} for {wanted.Architecture} failed: {installFailure}";
				return null;
			}

			registered = TryRegister(manifest, packageName, out var retried);
			if (registered is not null) return registered;

			failure = $"{retried} This is the second attempt, after installing {string.Join(", ", installed)} "
				+ $"for {wanted.Architecture} because the first was refused for want of {wanted.Name}.";
			return null;
		}
	}

	/// <summary>
	/// One attempt at registering the layout: the package family name, or null with whatever the
	/// deployment engine said, the deployment log's errors for that activity included.
	/// </summary>
	private static string? TryRegister(string manifest, string packageName, out string reported)
	{
		// The log is read here, while the activity id is in hand, because the exception's message is
		// the deployment engine's summary and the log is its whole account: a refusal whose message
		// names nothing useful still has the step that failed, and the package, in the log's errors.
		var script =
			$$"""
			$ProgressPreference = 'SilentlyContinue'
			try { Add-AppxPackage -Register {{Quoted(manifest)}} -ErrorAction Stop }
			catch
			{
				$message = $_.Exception.Message
				foreach ($line in $message -split '\r?\n') { if ($line.Trim()) { 'ERROR: ' + $line.Trim() } }
				if ($message -match '\[ActivityId\]\s+([0-9a-fA-F-]{36})')
				{
					try
					{
						Get-AppPackageLog -ActivityID $Matches[1] -ErrorAction Stop |
							Where-Object { $_.Level -le 2 } |
							ForEach-Object { 'LOG: ' + ($_.Message -replace '\s+', ' ').Trim() }
					}
					catch { 'LOG: the deployment log for that activity could not be read: ' + $_.Exception.Message }
				}
				exit 0
			}
			$p = Get-AppxPackage {{Quoted(packageName)}}
			if ($p) { 'PFN: ' + $p.PackageFamilyName }
			""";

		var (_, output) = RunPowerShell(script);
		var result = ReadRegistration(output);

		reported = result.Reported;
		return result.FamilyName;
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
	/// That sentence is on the third line of the exception's message, under a first line that says only
	/// "Package failed updates, dependency or conflict validation", so it is looked for in the whole
	/// report rather than at its start: read from the first line alone, every missing framework looks
	/// like a failure that is not about frameworks, and nothing is ever installed.
	/// <para>
	/// Parsing prose is not something to do lightly, and it earns it here: the alternative is a list of
	/// framework names kept in the fixture by hand, which is a guess about what the deployment engine
	/// wants rather than a reading of what it asked for, and it goes stale the first time a probe gains
	/// a dependency.
	/// </para>
	/// </remarks>
	internal static FrameworkDependency? MissingFramework(string reported)
	{
		var name = Regex.Match(reported, @"Provide the framework ""([^""]+)""");
		if (!name.Success) return null;

		var architecture = Regex.Match(reported, @"with neutral or (\w+) processor architecture");

		return new FrameworkDependency(
			name.Groups[1].Value,
			architecture.Success ? architecture.Groups[1].Value : RuntimeInformation.ProcessArchitecture.ToString());
	}

	/// <summary>A framework package a layout needs, as the deployment engine described it.</summary>
	internal readonly record struct FrameworkDependency(string Name, string Architecture);

	/// <summary>
	/// Installs every framework package the build's recipe holds for the architecture Windows asked
	/// for, plus the one it named from the Windows SDK where the recipe does not hold it. The names of
	/// what was installed, or null with why not.
	/// </summary>
	/// <remarks>
	/// Only the named architecture, because the recipe lists each framework for every architecture
	/// the project could target and installing an x86 framework does nothing for an x64 package.
	/// </remarks>
	private static IReadOnlyList<string>? InstallFrameworks(FrameworkDependency wanted, string? recipe, out string failure)
	{
		failure = string.Empty;

		var packages = RecipeFrameworks(recipe, wanted.Architecture);
		if (!packages.Any(package => string.Equals(package.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)))
		{
			if (SdkFramework(wanted) is not { } fromSdk)
			{
				failure = $"the build's recipe ({recipe ?? "none"}) holds no {wanted.Name} for {wanted.Architecture}, and no "
					+ $"package for it was found under the Windows SDK's ExtensionSDKs either. Install it by hand, or "
					+ "install the Visual Studio or Windows SDK component that ships it.";
				return null;
			}

			packages = [.. packages, new FrameworkPackage(wanted.Name, fromSdk)];
		}

		var absent = packages.Where(package => !File.Exists(package.Path)).ToList();
		if (absent.Count > 0)
		{
			failure = "the build names framework packages that are not on disk: "
				+ string.Join("; ", absent.Select(package => $"{package.Name} at {package.Path}"));
			return null;
		}

		// 0x80073D06 is "a higher version is already installed", which leaves the machine with what the
		// package needs; registering it again is what finds out whether it is enough.
		var script = "$ProgressPreference = 'SilentlyContinue'\n"
			+ string.Concat(packages.Select(package =>
				$"try {{ Add-AppxPackage -Path {Quoted(package.Path)} -ErrorAction Stop }} "
					+ "catch { if ($_.Exception.Message -notmatch '0x80073D06') "
					+ $"{{ 'ERROR: {package.Name.Replace("'", "''")} from ' + {Quoted(package.Path)} + ': ' + ($_.Exception.Message -replace '\\s+', ' ') }} }}\n"));

		var (_, output) = RunPowerShell(script);
		var errors = output.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.StartsWith("ERROR: ", StringComparison.Ordinal))
			.Select(line => line["ERROR: ".Length..])
			.ToList();

		if (errors.Count > 0)
		{
			failure = string.Join(" ", errors);
			return null;
		}

		return packages.Select(package => package.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>A framework package's identity name and the file it installs from.</summary>
	private readonly record struct FrameworkPackage(string Name, string Path);

	/// <summary>
	/// The framework packages a build's <c>.build.appxrecipe</c> resolved for one architecture, or none
	/// where there is no recipe.
	/// </summary>
	/// <remarks>
	/// Each is a <c>ResolvedSDKReference</c> carrying the framework's identity name, its architecture and
	/// an <c>AppxLocation</c>: the package Visual Studio's deploy would install beside the app. The paths
	/// are MSBuild-escaped -- <c>Program Files %28x86%29</c> -- and some climb out of a <c>build</c>
	/// folder with <c>..</c>, so both are undone before anything looks for the file.
	/// </remarks>
	private static List<FrameworkPackage> RecipeFrameworks(string? recipe, string architecture)
	{
		if (recipe is null || !File.Exists(recipe)) return [];

		XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";

		return XDocument.Load(recipe)
			.Descendants(ns + "ResolvedSDKReference")
			.Select(reference => (
				Name: reference.Element(ns + "Name")?.Value.Trim(),
				Architecture: reference.Element(ns + "Architecture")?.Value.Trim(),
				Location: reference.Element(ns + "AppxLocation")?.Value.Trim()))
			.Where(reference => !string.IsNullOrEmpty(reference.Name) && !string.IsNullOrEmpty(reference.Location))
			.Where(reference => string.Equals(reference.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
			.Select(reference => new FrameworkPackage(reference.Name!, Path.GetFullPath(Uri.UnescapeDataString(reference.Location!))))
			.Distinct()
			.ToList();
	}

	/// <summary>
	/// A framework package from the Windows SDK's <c>ExtensionSDKs</c>, or null where it has none.
	/// </summary>
	/// <remarks>
	/// The SDK ships these one folder per architecture, and the file names do not match the package
	/// names -- <c>Microsoft.VCLibs.140.00.Debug</c> is <c>Microsoft.VCLibs.arm64.Debug.14.00.appx</c>
	/// on disk. So the search matches on the parts that do carry over: the family (the name up to its
	/// version), the architecture folder, and whether the wanted package is the Debug flavour, which is
	/// a different package identity rather than a different build of one.
	/// </remarks>
	private static string? SdkFramework(FrameworkDependency wanted)
	{
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

		return roots
			.Where(Directory.Exists)
			.SelectMany(root => SafeFiles(Path.Combine(root, family), "*.appx"))
			.Where(path => path.Contains(wanted.Architecture, StringComparison.OrdinalIgnoreCase))
			.Where(path => path.Contains(".Debug", StringComparison.OrdinalIgnoreCase) == debug)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	/// <summary>
	/// What one registration attempt's output says: the package family name where it registered, and
	/// otherwise the deployment engine's whole account of why not.
	/// </summary>
	internal readonly record struct RegistrationOutput(string? FamilyName, string Reported);

	/// <summary>
	/// Reads the output of <see cref="TryRegister"/>'s script: <c>PFN:</c> on success, and on a refusal
	/// one <c>ERROR:</c> line per line of the exception's message and one <c>LOG:</c> line per error in
	/// the deployment log for that activity.
	/// </summary>
	/// <remarks>
	/// Every <c>ERROR:</c> line is kept, not the first. Add-AppxPackage's message is a headline, a blank
	/// line, the reason and a note naming the activity, and the headline alone -- "Package failed
	/// updates, dependency or conflict validation" -- is what a refusal for a missing framework looks
	/// like with the framework's name cut off.
	/// <para>
	/// A log error the message already says is left out, because the log repeats the reason twice --
	/// once as itself and once as "the specific error text for this failure is" -- and a failure that
	/// says the same sentence three times hides whatever else the log had to add.
	/// </para>
	/// </remarks>
	internal static RegistrationOutput ReadRegistration(string output)
	{
		const string Specific = "The specific error text for this failure is: ";

		var lines = output.Split('\n').Select(line => line.Trim()).ToArray();

		var pfn = lines.FirstOrDefault(line => line.StartsWith("PFN: ", StringComparison.Ordinal));
		if (pfn is not null) return new RegistrationOutput(pfn["PFN: ".Length..].Trim(), string.Empty);

		var message = string.Join(" ", Tagged("ERROR: "));
		var log = Tagged("LOG: ")
			.Where(line =>
			{
				var at = line.IndexOf(Specific, StringComparison.Ordinal);
				var reason = at < 0 ? line : line[(at + Specific.Length)..];
				return !message.Contains(reason, StringComparison.Ordinal);
			})
			.ToList();

		var saidNothing = message.Length == 0 && !Tagged("LOG: ").Any();
		if (saidNothing)
		{
			var said = output.Trim();
			return new RegistrationOutput(
				null,
				said.Length == 0
					? "Add-AppxPackage reported nothing and the package is not registered."
					: $"Add-AppxPackage reported no error and the package is not registered. PowerShell wrote: {said}");
		}

		var reported = log.Count == 0
			? message
			: $"{message} The deployment log for that activity also says: {string.Join(" ", log)}";
		return new RegistrationOutput(null, reported.Trim());

		IEnumerable<string> Tagged(string tag) =>
			lines.Where(line => line.StartsWith(tag, StringComparison.Ordinal)).Select(line => line[tag.Length..].Trim());
	}

	/// <summary>
	/// Runs a Windows PowerShell script, passed encoded so that no path or quote in it has to survive a
	/// command line.
	/// </summary>
	private static (int ExitCode, string Output) RunPowerShell(string script) => RunProcess(
		"powershell",
		$"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}");

	/// <summary>A string as a single-quoted PowerShell literal.</summary>
	private static string Quoted(string value) => $"'{value.Replace("'", "''")}'";

	/// <summary>
	/// The one gate every registration takes; see <see cref="RegisterAppxLayout"/> for why it is shared.
	/// </summary>
	private static readonly Lock RegistrationGate = new();

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
