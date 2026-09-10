using System.Runtime.InteropServices;

using static RoseMcp.IntegrationTests.TestToolchain;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The WinUI 3 probe app, built once for the whole test run in each of the two shapes it is tested
/// in, plus the native provider injected into it.
/// <para>
/// The sibling of <see cref="UwpProbeApp"/> and deliberately the same shape, for the same reason: the
/// four WinUI tests were each paying a <c>dotnet restore</c> and a <c>dotnet build</c> of the probe,
/// about twenty seconds apiece, to produce byte-identical output four times over.
/// </para>
/// <para>
/// It is a <em>separate</em> fixture rather than another lease shape on <see cref="UwpProbeApp"/>,
/// and that is the point rather than a filing decision. UWP's gate exists because two tests cannot
/// drive one single-instance app; a WinUI test drives a different process entirely and shares nothing
/// with it -- no package, no provider, no window. Putting them under one gate would serialise two
/// suites that have no reason to wait for each other, which is exactly the mistake #108 called out in
/// disabling parallelization on a whole class. With a gate each, the WinUI tests run alongside the
/// UWP ones and the suite pays for the longer of the two rather than their sum.
/// </para>
/// <para>
/// Lazy, like its sibling: an assembly fixture is constructed before any test runs, so nothing here
/// happens until a WinUI test actually asks for the app.
/// </para>
/// </summary>
public sealed class WinUiProbeApp : IAsyncDisposable
{
	private const string PackageName = "RoseMcp.ProbeApp.WinUi";

	private readonly Lock _gate = new();

	/// <summary>
	/// The built output directory per shape. Two entries at most: the packaged and unpackaged builds
	/// are different outputs of one project, so they are cached apart and neither is rebuilt.
	/// </summary>
	private readonly Dictionary<bool, string?> _built = [];

	private bool _registered;
	private string? _aumid;

	/// <summary>Why registration failed, so the skip can say it rather than guess at a cause.</summary>
	private string? _registrationFailure;

	/// <summary>
	/// Takes the WinUI probe for one test: makes sure everything it needs is built, and hands back
	/// where it was built. Skips the calling test where the machine cannot provide it.
	/// <para>
	/// Nothing here serialises the tests. A caller declares <c>[WinUiProbe]</c>, which is a constraint
	/// key nothing else in the suite holds, so WinUI tests queue behind each other and run alongside
	/// the classic and modern probes -- different packages, different processes, nothing shared to
	/// contend for. The shared build steps <see cref="Prepare"/> reaches are locked where they live.
	/// </para>
	/// </summary>
	/// <param name="packaged">Which shape to build. The packaged one is also registered.</param>
	/// <param name="needsXamlProvider">
	/// True for the tests that go on to read the visual tree, which need the native provider as well
	/// as the app. False for the two that only launch and debug it, so a machine with the .NET SDK
	/// but no C++ toolset still runs those.
	/// </param>
	/// <param name="cancellationToken">Observed before any building starts.</param>
	public async Task<Turn> TakeAsync(bool packaged, bool needsXamlProvider, CancellationToken cancellationToken)
	{
		await Task.Yield();
		cancellationToken.ThrowIfCancellationRequested();

		return new Turn(Prepare(packaged, needsXamlProvider), packaged ? _aumid : null);
	}

	private string Prepare(bool packaged, bool needsXamlProvider)
	{
		lock (_gate)
		{
			if (needsXamlProvider && !ProviderBuilt())
			{
				Skip.Test("The WinUI XAML provider could not be built (no C++ toolset, or no WindowsAppSDK).");
			}

			// The WinUI target runs natively, but the broker still hosts it out of the x64 host on x64.
			EnsureX64HostBuilt();

			if (!_built.TryGetValue(packaged, out var output))
			{
				output = Build(packaged);
				_built[packaged] = output;
			}

			if (output is null) Skip.Test("The WinUI probe app could not be restored (the WindowsAppSDK may be unavailable).");

			if (packaged && !_registered)
			{
				_registered = true;
				_aumid = Register(output!, out _registrationFailure);
			}

			if (packaged && _aumid is null)
			{
				Skip.Test($"The WinUI probe app could not be registered: {_registrationFailure}");
			}

			return output!;
		}
	}

	/// <summary>
	/// Builds the native provider once a run, through the shared gate. A different tap from the two
	/// UWP fixtures' -- WinUI 3's diagnostics live in a different framework DLL behind a different
	/// endpoint -- so it contends with them for nothing but the gate itself.
	/// </summary>
	private static bool ProviderBuilt() => EnsureXamlProviderBuilt("RoseMcp.Xaml.WinUi.Tap", ProviderPlatform());

	/// <summary>
	/// The platform the provider is built for. It must match the target, and a WinUI 3 app runs
	/// natively rather than emulated, so this follows the host rather than being pinned to x64 the way
	/// the UWP provider is.
	/// </summary>
	private static string ProviderPlatform() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		_ => "x64",
	};

	/// <summary>
	/// The RID the probe is built for. Unlike classic UWP, which has no ARM64 runtime and is debugged
	/// x64-emulated, WinUI 3 runs natively -- so the probe matches the host and the live-app host needs
	/// no architecture shim.
	/// </summary>
	private static string RuntimeIdentifier() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "win-arm64",
		_ => "win-x64",
	};

	private static string ProbeDirectory() => Path.Combine(RepositoryRoot(), "tests", "apps", "winui");

	/// <summary>
	/// Builds the probe in one of its two shapes and returns the directory holding the built app. Null
	/// only when restore fails, which is the environment limit worth skipping on: the app needs the
	/// WindowsAppSDK from NuGet, and a machine that cannot get it cannot run these tests.
	/// <para>
	/// Restore and build are separate calls precisely so those two can be told apart. A build that
	/// fails <em>after</em> a good restore is our own breakage and throws: a capability quietly not
	/// being tested is worse than a red build, and looks identical to a machine that cannot build it.
	/// </para>
	/// </summary>
	private static string? Build(bool packaged)
	{
		var project = Path.Combine(ProbeDirectory(), "Rose.ProbeApp.WinUi.csproj");
		var rid = RuntimeIdentifier();
		var packagedArgument = packaged ? " -p:ProbePackaged=true" : string.Empty;

		var restore = RunProcess("dotnet", $"restore \"{project}\" -r {rid}{packagedArgument}");
		if (restore.ExitCode != 0) return null;

		var build = RunProcess(
			"dotnet",
			$"build \"{project}\" -r {rid} -c {Configuration()} --no-restore{packagedArgument} -v:minimal -nologo");

		if (build.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"Building the WinUI probe app failed (exit {build.ExitCode}):{Environment.NewLine}{build.Output}");
		}

		// Each shape has its own output root, so this cannot pick up the other one's binary. Sharing
		// them is what let an unpackaged caller launch a packaged build and die on REGDB_E_CLASSNOTREG
		// before showing a window, which reads as a broken machine rather than a build collision (#180).
		var bin = packaged
			? Path.Combine(ProbeDirectory(), "bin", "packaged", Configuration())
			: Path.Combine(ProbeDirectory(), "bin", Configuration());
		var exe = !Directory.Exists(bin)
			? null
			: Directory.EnumerateFiles(bin, "Rose.ProbeApp.WinUi.exe", SearchOption.AllDirectories)
				.Where(path => path.Contains(rid, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.FirstOrDefault();

		if (exe is null)
		{
			throw new InvalidOperationException($"The WinUI probe build reported success but produced no exe for {rid} under {bin}.");
		}

		return Path.GetDirectoryName(exe);
	}

	/// <summary>
	/// Registers the packaged layout and returns its AUMID, or null with the reason it could not.
	/// <para>
	/// The layout is the build output directory itself: a WinUI 3 desktop build writes
	/// AppxManifest.xml beside the exe, with none of the staging the classic UWP probe needs, because
	/// it has no split between a managed assembly and a native CoreCLR apphost.
	/// </para>
	/// </summary>
	private static string? Register(string layoutDirectory, out string? failure)
	{
		var family = RegisterAppxLayout(Path.Combine(layoutDirectory, "AppxManifest.xml"), PackageName, out failure);

		return family is null ? null : $"{family}!App";
	}

	/// <summary>
	/// One test's turn with the probe. <see cref="Directory"/> is where the app was built;
	/// <see cref="Aumid"/> is set only for the packaged shape.
	/// </summary>
	public sealed class Turn(string directory, string? aumid)
	{
		public string Directory => directory;

		public string? Aumid => aumid;

		/// <summary>The probe executable, which is what the unpackaged tests launch and attach to.</summary>
		public string ExecutablePath => Path.Combine(directory, "Rose.ProbeApp.WinUi.exe");
	}

	/// <summary>
	/// Removes the package this suite registered on the machine. An assembly fixture is what gives
	/// that an end-of-run teardown, which the per-test Remove-AppxPackage calls used to do.
	/// </summary>
	public ValueTask DisposeAsync()
	{
		if (_registered)
		{
			RunProcess("powershell", $"-NoProfile -NonInteractive -Command \"Get-AppxPackage '{PackageName}' | Remove-AppxPackage -ErrorAction SilentlyContinue\"");
		}

		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Whether this probe has ever come up in this run, which is what separates a machine that cannot
	/// run these tests from an app that died this time.
	/// <para>
	/// Before the first success a launch failure is a fact about the machine and skips; after it, the
	/// same failure is a test that silently did not run, which is the one outcome an acceptance test
	/// must not report as green. A machine where the Windows App Runtime never bootstraps (#180) never
	/// sets this and goes on skipping.
	/// </para>
	/// </summary>
	public bool HasLaunched { get; private set; }

	/// <summary>Records that the app came up, which arms the rule above for the rest of the run.</summary>
	public void NoteLaunched() => HasLaunched = true;
}
