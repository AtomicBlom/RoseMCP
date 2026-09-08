using System.Runtime.InteropServices;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The UWP-on-modern-.NET probe app, built and registered once for the whole test run, plus the
/// native provider injected into it.
/// <para>
/// The third fixture of this shape, and it earns being a fixture rather than a lease on
/// <see cref="UwpProbeApp"/> for the reason <see cref="WinUiProbeApp"/> already sets out: a gate
/// exists to stop two tests driving one single-instance app, and this is a different package and a
/// different process, sharing no window, no registration and no instance with the classic probe.
/// Under one gate the two UWP suites would serialise against each other for no reason; under a gate
/// each they overlap and the suite pays for the longer rather than the sum.
/// </para>
/// <para>
/// What it is <em>for</em> is narrower than "UWP again". The XAML framework is the same
/// Windows.UI.Xaml the classic probe uses, and the live half treats them identically -- measured, in
/// the sense that the tap, the endpoint and the dispatcher seam all served this app unmodified. What
/// differs is underneath: CoreCLR through a CsWinRT projection rather than the classic project's
/// CoreCLR-or-.NET-Native split, which puts the app's managed frames behind an ABI layer and makes
/// architectures other than x64 debuggable for the first time. None of that is reachable from
/// uwp-classic, which can only ever be Debug x64.
/// </para>
/// <para>
/// Lazy, like both siblings: an assembly fixture is constructed before any test runs, so nothing
/// here happens until a modern UWP test actually asks for the app.
/// </para>
/// </summary>
public sealed class UwpModernProbeApp : IAsyncDisposable
{
	private const string PackageName = "RoseMcp.ProbeApp.UwpModern";

	/// <summary>The layout's executable, which is also its process name -- what <see cref="StopApp"/> kills.</summary>
	private const string ProcessName = "Rose.ProbeApp.UwpModern";

	/// <summary>
	/// One modern UWP test at a time. The app is single-instance, and activating it while an instance
	/// is running foregrounds that instance rather than launching a process -- so a from-birth
	/// debugger would wait for a startup that never comes, which is the failure
	/// <c>Uwp.FindRunningProcesses</c> exists to explain.
	/// </summary>
	private readonly SemaphoreSlim _oneAtATime = new(1, 1);

	private readonly Lock _gate = new();

	private bool _builtProbed;
	private string? _built;
	private bool _msBuildProbed;
	private string? _msBuild;
	private bool _registered;
	private string? _aumid;

	/// <summary>
	/// Takes the modern UWP probe for one test: waits its turn, makes sure everything it needs is
	/// built and registered, and hands back the AUMID. Skips the calling test where the machine cannot
	/// provide it.
	/// </summary>
	/// <param name="needsXamlProvider">
	/// True for the tests that go on to read the visual tree, which need the native provider as well
	/// as the app. False for those that only launch and debug it, so a machine with the .NET SDK and
	/// the Windows SDK but no C++ toolset still runs those.
	/// </param>
	/// <param name="cancellationToken">Cancels waiting for the turn.</param>
	public async Task<Turn> TakeAsync(bool needsXamlProvider, CancellationToken cancellationToken)
	{
		await _oneAtATime.WaitAsync(cancellationToken);

		try
		{
			var (directory, aumid) = Prepare(needsXamlProvider);
			return new Turn(this, directory, aumid);
		}
		catch
		{
			// A skip throws, and a turn nobody holds must not be left locked (#113).
			_oneAtATime.Release();
			throw;
		}
	}

	private (string Directory, string Aumid) Prepare(bool needsXamlProvider)
	{
		lock (_gate)
		{
			if (needsXamlProvider && !ProviderBuilt())
			{
				Skip.Test("The UWP XAML provider could not be built (no C++ toolset, or no Windows SDK).");
			}

			// A modern UWP app runs as whatever it was built for, so the host matches it. On x64 that is
			// the x64 host, which a plain solution build does not produce.
			if (RuntimeInformation.ProcessArchitecture == Architecture.X64) EnsureX64HostBuilt();

			if (!_builtProbed)
			{
				_builtProbed = true;
				_built = Build();
			}

			if (_built is null)
			{
				Skip.Test(
					"The modern UWP probe app could not be built: it needs full MSBuild from a Visual Studio "
						+ "install and the Windows SDK's XAML compiler, which dotnet build cannot substitute for.");
			}

			if (!_registered)
			{
				_registered = true;
				_aumid = Register(_built!);
			}

			if (_aumid is null) Skip.Test("The modern UWP probe app could not be registered (developer mode may be off).");

			return (_built!, _aumid!);
		}
	}

	/// <summary>
	/// Builds the native provider once a run, through the shared gate. It is the <em>same</em> UWP tap
	/// the classic probe injects, because it is the same XAML framework -- so the build is shared and
	/// must not be raced, which is what <see cref="TestToolchain.EnsureXamlProviderBuilt"/> is for.
	/// <para>
	/// The classic probe's provider is pinned to x64 because classic UWP has no ARM64 runtime and is
	/// debugged emulated. This one follows the host, because modern UWP runs natively. On x64 that
	/// means both fixtures ask for the same build and the second one gets the first one's answer.
	/// </para>
	/// </summary>
	private static bool ProviderBuilt() => EnsureXamlProviderBuilt("RoseMcp.Xaml.Uwp.Tap", ProviderPlatform());

	private static string ProviderPlatform() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		_ => "x64",
	};

	/// <summary>The MSBuild platform name, which is the RID's architecture rather than a RID.</summary>
	private static string BuildPlatform() => ProviderPlatform();

	private static string RuntimeIdentifier() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "win-arm64",
		_ => "win-x64",
	};

	private static string ProbeDirectory() => Path.Combine(RepositoryRoot(), "tests", "apps", "uwp-modern");

	/// <summary>
	/// Builds the probe and returns the directory holding the built app, or null when the machine has
	/// no toolchain for it.
	/// <para>
	/// <c>dotnet build</c> is deliberately not used, and this is the one thing about modern UWP that
	/// most looks like it should work and does not. The UWP XAML markup compiler runs only under full
	/// MSBuild; under the .NET SDK's it does not run and does not complain, and the build fails with
	/// CS0103 on InitializeComponent and CS5001 for a missing entry point -- which reads like broken
	/// source rather than a wrong toolchain, and would be diagnosed as a bug in this app.
	/// </para>
	/// </summary>
	private string? Build()
	{
		var msbuild = MsBuild();
		if (msbuild is null) return null;

		var csproj = Path.Combine(ProbeDirectory(), "Rose.ProbeApp.UwpModern.csproj");
		var platform = BuildPlatform();
		var rid = RuntimeIdentifier();
		var properties = $"-p:Configuration={Configuration()} -p:Platform={platform} -p:RuntimeIdentifier={rid}";

		var restore = RunProcess(msbuild, $"\"{csproj}\" -t:Restore {properties} -v:minimal -nologo");
		if (restore.ExitCode != 0) return null;

		var build = RunProcess(msbuild, $"\"{csproj}\" -t:Build {properties} -v:minimal -nologo");
		if (build.ExitCode != 0)
		{
			// Restore succeeded, so the toolchain is present and this is our own breakage. A capability
			// quietly not being tested is worse than a red build, and looks identical to a machine that
			// cannot build it.
			throw new InvalidOperationException(
				$"Building the modern UWP probe app failed (exit {build.ExitCode}):{Environment.NewLine}{build.Output}");
		}

		var output = Path.Combine(ProbeDirectory(), "bin", platform, Configuration(), "net10.0-windows10.0.26100.0", rid);
		if (!File.Exists(Path.Combine(output, "AppxManifest.xml")))
		{
			throw new InvalidOperationException($"The modern UWP probe build reported success but produced no AppxManifest.xml under {output}.");
		}

		return output;
	}

	/// <summary>
	/// Registers the build output directly and returns its AUMID, or null where registration is not
	/// permitted, so the test skips rather than failing on an environment limit.
	/// <para>
	/// The build output <em>is</em> the runnable layout, which is the sharpest break from the classic
	/// probe. That one produces a managed assembly and a native CoreCLR apphost in different folders
	/// and needs its AppX layout staged from a build recipe before anything can register it. A modern
	/// UWP build writes AppxManifest.xml beside a native apphost and coreclr.dll in one flat,
	/// self-contained folder, so registering it needs no staging at all.
	/// </para>
	/// </summary>
	private static string? Register(string layoutDirectory)
	{
		var manifest = Path.Combine(layoutDirectory, "AppxManifest.xml");
		if (!File.Exists(manifest)) return null;

		var script =
			$"try {{ Add-AppxPackage -Register '{manifest}' -ErrorAction Stop }} catch {{ Write-Output ('ERROR: ' + $_.Exception.Message); exit 0 }}; "
				+ $"$p = Get-AppxPackage '{PackageName}'; if ($p) {{ Write-Output ('PFN: ' + $p.PackageFamilyName) }}";
		var (_, output) = RunProcess("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");

		var pfnLine = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("PFN: ", StringComparison.Ordinal));
		if (pfnLine is null) return null;

		return $"{pfnLine["PFN: ".Length..].Trim()}!App";
	}

	/// <summary>
	/// Ends any running instance. A turn has to give the app back closed, because activating a
	/// single-instance app that is already running foregrounds the existing window instead of starting
	/// a process -- so the next test's from-birth debugger would wait for a startup that never comes.
	/// </summary>
	private static void StopApp()
	{
		RunProcess(
			"powershell",
			$"-NoProfile -NonInteractive -Command \"Get-Process -Name '{ProcessName}' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\"");
	}

	/// <summary>
	/// The MSBuild that can build a UseUwp project, or null when this machine has none. Probed once,
	/// including the null.
	/// <para>
	/// Two things are required and they come from different installs. MSBuild itself comes from Visual
	/// Studio; the XAML markup compiler comes from the Windows SDK, as
	/// <c>Windows Kits\10\bin\&lt;version&gt;\XamlCompiler\Microsoft.Windows.UI.Xaml.Build.Tasks.dll</c>.
	/// The classic probe checks for the WindowsXaml <em>targets</em> under the VS install instead,
	/// which is the right check for a project that imports them by path and the wrong one here: a
	/// UseUwp project reaches the compiler through the .NET SDK, so the VS targets folder says nothing
	/// about whether it can build.
	/// </para>
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

		return _msBuild = XamlCompilerPresent() ? msbuild : null;
	}

	/// <summary>Whether any installed Windows SDK carries the UWP XAML markup compiler.</summary>
	private static bool XamlCompilerPresent()
	{
		var bin = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
		if (!Directory.Exists(bin)) return false;

		return Directory.EnumerateDirectories(bin).Any(version =>
			File.Exists(Path.Combine(version, "XamlCompiler", "Microsoft.Windows.UI.Xaml.Build.Tasks.dll")));
	}

	/// <summary>
	/// One test's turn with the probe. It ends with the app closed, whether the test closed it or not.
	/// </summary>
	public sealed class Turn(UwpModernProbeApp probe, string directory, string aumid) : IDisposable
	{
		/// <summary>Where the app was built, which is also the registered layout.</summary>
		public string Directory => directory;

		public string Aumid => aumid;

		public void Dispose()
		{
			StopApp();
			probe._oneAtATime.Release();
		}
	}

	/// <summary>
	/// Removes the package this suite registered on the machine, and ends anything still running.
	/// </summary>
	public ValueTask DisposeAsync()
	{
		if (_registered)
		{
			StopApp();
			RunProcess(
				"powershell",
				$"-NoProfile -NonInteractive -Command \"Get-AppxPackage '{PackageName}' | Remove-AppxPackage -ErrorAction SilentlyContinue\"");
		}

		return ValueTask.CompletedTask;
	}
}
