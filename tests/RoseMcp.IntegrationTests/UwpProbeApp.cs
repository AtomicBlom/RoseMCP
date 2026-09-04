using System.Diagnostics;
using System.Xml.Linq;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The classic UWP probe app, built, staged and registered once for the whole test run.
/// <para>
/// It used to be all of that per test, twenty times over: a vswhere probe, a PowerShell-driven MSVC
/// build of the native provider, an <c>msbuild -t:Restore</c> and an <c>msbuild -t:Build</c> of the
/// app, a layout stage that deleted and re-copied every packaged file, an <c>Add-AppxPackage</c>, and
/// a <c>Remove-AppxPackage</c> on the way out. All of it produces byte-identical output every time,
/// and all of it ran inside one test class -- which is one xUnit collection, so it ran serially. That
/// made the twenty UWP tests, rather than the hundred and sixty Roslyn solution loads, the thing the
/// suite's wall clock was actually made of.
/// </para>
/// <para>
/// The existing x64-host cache in <see cref="TestToolchain.EnsureX64Build"/> is the precedent, and its
/// reasoning carries: MSBuild is incremental, so paying for the check once a run costs almost nothing,
/// and paying for it once means it is still a real build rather than a stale exe being "found".
/// </para>
/// <para>
/// Lazy on purpose. An assembly fixture is constructed before any test in the assembly runs, so doing
/// the work in a constructor or <c>InitializeAsync</c> would make every filtered run of one Roslyn
/// test pay for a UWP toolchain build. Nothing here happens until a UWP test asks for an AUMID.
/// </para>
/// </summary>
public sealed class UwpProbeApp : IDisposable
{
	private const string PackageName = "RoseMcp.ProbeApp.UwpClassic";

	/// <summary>The layout's executable, which is also its process name -- what <see cref="StopApp"/> kills.</summary>
	private const string ProcessName = "Rose.ProbeApp.UwpClassic";

	private readonly Lock _gate = new();

	/// <summary>
	/// One UWP test at a time. Held here rather than by disabling parallelization on the test class,
	/// which sounds like the same thing and is not: that stops the class running in parallel with
	/// <em>anything</em>, and it was measured -- one of two hundred and ten other tests overlapped a
	/// live-app test, so the suite's two halves added up (268s + 109s) instead of overlapping. What
	/// actually cannot overlap is two tests driving this one app, so that is what is serialised.
	/// </summary>
	private readonly SemaphoreSlim _oneAtATime = new(1, 1);

	private bool _msBuildProbed;
	private string? _msBuild;
	private bool _providerProbed;
	private bool _providerBuilt;
	private bool _registered;
	private string? _aumid;
	private string? _layoutDirectory;

	/// <summary>
	/// The AUMID of a registered, launchable probe app, having built everything it needs. Skips the
	/// calling test where the environment cannot provide it, which is the same three skips these tests
	/// each spelled out for themselves.
	/// </summary>
	/// <param name="needsXamlProvider">
	/// True for the tests that go on to read or edit the visual tree, which need the native provider
	/// as well as the app. False for the two that only launch and debug it -- a machine with the UWP
	/// tooling but no C++ toolset should still run those.
	/// </param>
	private string AumidCore(bool needsXamlProvider)
	{
		lock (_gate)
		{
			var msbuild = MsBuild();
			if (msbuild is null) Assert.Skip("No Visual Studio MSBuild with the classic-UWP tooling was found.");

			if (needsXamlProvider && !ProviderBuilt())
			{
				Assert.Skip("The native XAML provider could not be built (no C++ toolset).");
			}

			// The UWP target is x64 (emulated on ARM64), so the broker needs the x64 host present.
			EnsureX64HostBuilt();

			if (_registered)
			{
				if (_aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");
				return _aumid!;
			}

			_registered = true;
			_layoutDirectory = Stage(Build(msbuild!));
			_aumid = Register(_layoutDirectory);

			if (_aumid is null) Assert.Skip("The UWP probe app could not be registered (developer mode may be off).");
			return _aumid!;
		}
	}

	/// <summary>
	/// The staged AppX layout the package is registered from, which is also the install location a
	/// UWP session reports. Null until <see cref="LeaseAsync"/> has staged it.
	/// </summary>
	public string? LayoutDirectory => _layoutDirectory;

	/// <summary>
	/// Takes the app for one test: waits its turn, makes sure everything is built and registered, and
	/// hands back the AUMID to launch. Disposing the lease ends the app and lets the next test in.
	/// <para>
	/// A lease rather than a getter plus a <c>finally</c>, because the two have to go together. The
	/// turn is only safely held while the app is nobody else's, and the thing that ends the app is the
	/// thing that ends the turn.
	/// </para>
	/// </summary>
	public async Task<Lease> LeaseAsync(bool needsXamlProvider, CancellationToken cancellationToken)
	{
		await _oneAtATime.WaitAsync(cancellationToken);
		try
		{
			// Inside the turn, so the build below is never entered by two tests at once and the lock
			// it takes is never contended -- which matters because it is a synchronous lock held
			// across a half-minute of MSBuild, and twenty xUnit threads queued on it would be twenty
			// threads not running Roslyn tests.
			return new Lease(this, AumidCore(needsXamlProvider));
		}
		catch
		{
			// Including the skip, which is an exception here. A turn nobody is using must not be kept.
			_oneAtATime.Release();
			throw;
		}
	}

	/// <summary>
	/// One test's turn with the app. Disposing it ends the app and then releases the turn, in that
	/// order: the next test launches by AUMID, and a surviving instance would be activated rather
	/// than started under the debugger, so the turn must not be handed on while the app is still up.
	/// <para>
	/// Each test also stops the app in its own <c>finally</c>, which is what ends it promptly rather
	/// than at scope exit. Stopping is idempotent, so this is the backstop for the case that finally
	/// cannot cover: a test written without one.
	/// </para>
	/// </summary>
	public sealed class Lease(UwpProbeApp probe, string aumid) : IDisposable
	{
		public string Aumid { get; } = aumid;

		public void Dispose()
		{
			probe.StopApp();
			probe._oneAtATime.Release();
		}
	}
	/// <summary>
	/// Ends the running app, so the next test's launch starts a fresh process rather than activating
	/// this one.
	/// <para>
	/// This is the half of the old per-test <c>Remove-AppxPackage</c> that was load-bearing and is easy
	/// to lose sight of: unregistering also terminated the app. A packaged app is single-instance, so
	/// launching by AUMID with the previous instance still up activates that one instead of starting
	/// one under the debugger -- and a from-birth attach then has nothing to attach to.
	/// </para>
	/// </summary>
	public void StopApp()
	{
		foreach (var process in Process.GetProcessesByName(ProcessName))
		{
			try
			{
				if (!process.HasExited) process.Kill(entireProcessTree: true);
				process.WaitForExit(5000);
			}
			catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				// It exited between the enumeration and the kill, or it is already going. Either way
				// the postcondition holds.
			}
			finally
			{
				process.Dispose();
			}
		}
	}

	/// <summary>
	/// Unregisters the package, once, after every test in the assembly. Nothing to do where no test
	/// ever asked for it, which is every run on a machine without the UWP tooling.
	/// </summary>
	public void Dispose()
	{
		lock (_gate)
		{
			if (!_registered) return;

			StopApp();
			RunProcess(
				"powershell",
				$"-NoProfile -NonInteractive -Command \"Get-AppxPackage '{PackageName}' | Remove-AppxPackage -ErrorAction SilentlyContinue\"");
			_registered = false;
		}
	}

	/// <summary>
	/// The MSBuild that can build classic UWP, found via vswhere, or null when no such Visual Studio is
	/// installed. Probed once, including the null: twenty vswhere processes to reach the same answer is
	/// the cheapest of the things this class stopped repeating, and still not free.
	/// </summary>
	private string? MsBuild()
	{
		if (_msBuildProbed) return _msBuild;
		_msBuildProbed = true;

		var vswhere = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			"Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (!File.Exists(vswhere)) return null;

		var (exitCode, output) = RunProcess(
			vswhere,
			"-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe");
		if (exitCode != 0) return null;

		var msbuild = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.EndsWith("MSBuild.exe", StringComparison.OrdinalIgnoreCase));
		if (msbuild is null || !File.Exists(msbuild)) return null;

		// MSBuild alone is not enough; the classic-UWP C# targets must be installed too.
		var windowsXaml = Path.Combine(Path.GetDirectoryName(msbuild)!, "..", "..", "..", "MSBuild", "Microsoft", "WindowsXaml");
		return _msBuild = Directory.Exists(Path.GetFullPath(windowsXaml)) ? msbuild : null;
	}

	/// <summary>
	/// Builds the native XAML diagnostics provider (x64) with build.ps1. Returns false only when the
	/// toolchain is genuinely absent, so the caller skips; anything else throws.
	/// <para>
	/// That distinction is the point. This used to return false for any non-zero exit and the caller
	/// skipped with the message "no C++ toolset", which meant a compile error in the provider -- or
	/// two builds racing over one PDB, which is how it was noticed -- silently skipped the XAML tests
	/// and left the suite green. A capability quietly not being tested is worse than a red build, and
	/// looks identical to a machine that simply cannot build it. build.ps1 already separates the two:
	/// it exits 3 from its own Fail for a missing toolset or SDK, and anything else is a real failure.
	/// </para>
	/// <para>
	/// Building it once also removes that PDB race by construction rather than by luck.
	/// </para>
	/// </summary>
	private bool ProviderBuilt()
	{
		if (_providerProbed) return _providerBuilt;
		_providerProbed = true;

		var script = Path.Combine(RepositoryRoot(), "src", "RoseMcp.Xaml.Uwp.Tap", "build.ps1");
		if (!File.Exists(script)) return false;

		var (exitCode, output) = RunProcess(
			"powershell",
			$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Platform x64 -Configuration Debug");

		// 3 is build.ps1's Fail: no MSVC toolset, or no Windows SDK. The only skippable outcome.
		if (exitCode == 3) return false;

		if (exitCode != 0)
		{
			throw new InvalidOperationException(
				$"Building the XAML provider failed (exit {exitCode}):{Environment.NewLine}{output}");
		}

		var dll = Path.Combine(RepositoryRoot(), "src", "RoseMcp.Xaml.Uwp.Tap", "bin", "x64", "Debug", "RoseMcp.Xaml.Uwp.Tap.dll");
		if (!File.Exists(dll))
		{
			throw new InvalidOperationException($"The XAML provider build reported success but produced no {dll}.");
		}

		return _providerBuilt = true;
	}

	private static string AppDirectory() => Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-classic");

	/// <summary>Builds the classic UWP probe app Debug|x64 and returns its build output directory.</summary>
	private static string Build(string msbuild)
	{
		var csproj = Path.Combine(AppDirectory(), "Rose.ProbeApp.UwpClassic.csproj");

		var restore = RunProcess(msbuild, $"\"{csproj}\" -t:Restore -p:Configuration=Debug -p:Platform=x64 -v:minimal -nologo");
		if (restore.ExitCode != 0) throw new InvalidOperationException($"UWP restore failed:{Environment.NewLine}{restore.Output}");

		var build = RunProcess(msbuild, $"\"{csproj}\" -t:Build -p:Configuration=Debug -p:Platform=x64 -v:minimal -nologo");
		if (build.ExitCode != 0) throw new InvalidOperationException($"UWP build failed:{Environment.NewLine}{build.Output}");

		return Path.Combine(AppDirectory(), "bin", "x64", "Debug");
	}

	/// <summary>Stages the deployable AppX layout the way Visual Studio's deploy stages it.</summary>
	private static string Stage(string buildOutputDirectory)
	{
		var recipePath = Path.Combine(buildOutputDirectory, "Rose.ProbeApp.UwpClassic.build.appxrecipe");
		if (!File.Exists(recipePath)) throw new InvalidOperationException($"No appxrecipe at {recipePath}; the UWP build did not complete.");

		XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
		var recipe = XDocument.Load(recipePath);

		var layoutText = recipe.Descendants(ns + "LayoutDir").FirstOrDefault()?.Value
			?? throw new InvalidOperationException("The appxrecipe declares no LayoutDir.");
		var layoutDirectory = Uri.UnescapeDataString(layoutText);

		if (Directory.Exists(layoutDirectory)) Directory.Delete(layoutDirectory, recursive: true);
		Directory.CreateDirectory(layoutDirectory);

		// Both the manifest and every packaged file carry an Include (the source on disk, MSBuild-escaped)
		// and a PackagePath (where it lands in the layout).
		var entries = recipe.Descendants(ns + "AppXManifest").Concat(recipe.Descendants(ns + "AppxPackagedFile"));
		foreach (var entry in entries)
		{
			var source = Uri.UnescapeDataString(entry.Attribute("Include")!.Value);
			var packagePath = Uri.UnescapeDataString(entry.Element(ns + "PackagePath")!.Value);
			var destination = Path.Combine(layoutDirectory, packagePath);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(source, destination, overwrite: true);
		}

		return layoutDirectory;
	}

	/// <summary>
	/// Registers the loose UWP layout and returns its AUMID, or null when registration is not permitted
	/// (developer mode off), so the test can skip rather than fail on an environment limit.
	/// </summary>
	private static string? Register(string layoutDirectory)
	{
		var manifest = Path.Combine(layoutDirectory, "AppxManifest.xml");
		var script =
			$"try {{ Add-AppxPackage -Register '{manifest}' -ErrorAction Stop }} catch {{ Write-Output ('ERROR: ' + $_.Exception.Message); exit 0 }}; "
				+ $"$p = Get-AppxPackage '{PackageName}'; if ($p) {{ Write-Output ('PFN: ' + $p.PackageFamilyName) }}";
		var (_, output) = RunProcess("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");

		var pfnLine = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("PFN: ", StringComparison.Ordinal));
		if (pfnLine is null) return null;

		return $"{pfnLine["PFN: ".Length..].Trim()}!App";
	}
}
