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

	/// <summary>
	/// One WinUI test at a time. The packaged shape is single-instance, and the unpackaged tests each
	/// launch and debug their own process -- cheap to serialise, and it keeps a failure in one from
	/// presenting as a mystery in another.
	/// </summary>
	private readonly SemaphoreSlim _oneAtATime = new(1, 1);

	private readonly Lock _gate = new();

	/// <summary>
	/// The built output directory per shape. Two entries at most: the packaged and unpackaged builds
	/// are different outputs of one project, so they are cached apart and neither is rebuilt.
	/// </summary>
	private readonly Dictionary<bool, string?> _built = [];

	private bool _providerProbed;
	private bool _providerBuilt;
	private bool _registered;
	private string? _aumid;

	/// <summary>
	/// Takes the WinUI probe for one test: waits its turn, makes sure everything it needs is built,
	/// and hands back where it was built. Skips the calling test where the machine cannot provide it.
	/// </summary>
	/// <param name="packaged">Which shape to build. The packaged one is also registered.</param>
	/// <param name="needsXamlProvider">
	/// True for the tests that go on to read the visual tree, which need the native provider as well
	/// as the app. False for the two that only launch and debug it, so a machine with the .NET SDK
	/// but no C++ toolset still runs those.
	/// </param>
	/// <param name="cancellationToken">Cancels waiting for the turn.</param>
	public async Task<Turn> TakeAsync(bool packaged, bool needsXamlProvider, CancellationToken cancellationToken)
	{
		await _oneAtATime.WaitAsync(cancellationToken);

		try
		{
			return new Turn(this, Prepare(packaged, needsXamlProvider), packaged ? _aumid : null);
		}
		catch
		{
			// A skip throws, and a turn nobody holds must not be left locked.
			_oneAtATime.Release();
			throw;
		}
	}

	private string Prepare(bool packaged, bool needsXamlProvider)
	{
		lock (_gate)
		{
			if (needsXamlProvider && !ProviderBuilt())
			{
				Assert.Skip("The WinUI XAML provider could not be built (no C++ toolset, or no WindowsAppSDK).");
			}

			// The WinUI target runs natively, but the broker still hosts it out of the x64 host on x64.
			EnsureX64HostBuilt();

			if (!_built.TryGetValue(packaged, out var output))
			{
				output = Build(packaged);
				_built[packaged] = output;
			}

			if (output is null) Assert.Skip("The WinUI probe app could not be restored (the WindowsAppSDK may be unavailable).");

			if (packaged && !_registered)
			{
				_registered = true;
				_aumid = Register(output!);
			}

			if (packaged && _aumid is null) Assert.Skip("The WinUI probe app could not be registered (developer mode may be off).");

			return output!;
		}
	}

	/// <summary>
	/// Builds the native provider once a run. False where the machine simply cannot, which is
	/// build.ps1's exit 3, so the calling test skips rather than going red on an environment limit.
	/// </summary>
	private bool ProviderBuilt()
	{
		if (_providerProbed) return _providerBuilt;

		_providerProbed = true;

		var script = Path.Combine(RepositoryRoot(), "src", "RoseMcp.Xaml.WinUi.Tap", "build.ps1");
		if (!File.Exists(script)) return false;

		var (exitCode, output) = RunProcess(
			"powershell",
			$"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Platform {ProviderPlatform()} -Configuration Debug");

		// 3 is build.ps1's Fail: no MSVC toolset, no Windows SDK, or no WindowsAppSDK to project from.
		if (exitCode == 3) return false;

		if (exitCode != 0)
		{
			throw new InvalidOperationException(
				$"Building the WinUI XAML provider failed (exit {exitCode}):{Environment.NewLine}{output}");
		}

		var dll = Path.Combine(
			RepositoryRoot(), "src", "RoseMcp.Xaml.WinUi.Tap", "bin", ProviderPlatform(), "Debug", "RoseMcp.Xaml.WinUi.Tap.dll");
		if (!File.Exists(dll))
		{
			throw new InvalidOperationException($"The WinUI XAML provider build reported success but produced no {dll}.");
		}

		_providerBuilt = true;
		return true;
	}

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

		var bin = Path.Combine(ProbeDirectory(), "bin", Configuration());
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
	/// Registers the packaged layout and returns its AUMID, or null where registration is not
	/// permitted, so the test skips rather than failing on an environment limit.
	/// <para>
	/// The layout is the build output directory itself: a WinUI 3 desktop build writes
	/// AppxManifest.xml beside the exe, with none of the staging the classic UWP probe needs, because
	/// it has no split between a managed assembly and a native CoreCLR apphost.
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
	/// One test's turn with the probe. <see cref="Directory"/> is where the app was built;
	/// <see cref="Aumid"/> is set only for the packaged shape.
	/// </summary>
	public sealed class Turn(WinUiProbeApp probe, string directory, string? aumid) : IDisposable
	{
		public string Directory => directory;

		public string? Aumid => aumid;

		/// <summary>The probe executable, which is what the unpackaged tests launch and attach to.</summary>
		public string ExecutablePath => Path.Combine(directory, "Rose.ProbeApp.WinUi.exe");

		public void Dispose() => probe._oneAtATime.Release();
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
}
