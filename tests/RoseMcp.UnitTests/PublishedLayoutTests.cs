using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Logging;

namespace RoseMcp.UnitTests;

/// <summary>
/// That the hosts can find one another in the layout a deploy actually produces.
/// <para>
/// Every resolver here walks up to RoseMcp.slnx and searches bin when the published location is
/// empty, and inside the repository that fallback always succeeds -- so the suite could not tell a
/// working install from a broken one, and three defects lived only on an install. The fallback is
/// switched off here, which is the whole point: what is being tested is the arrangement, not the
/// machine this runs on.
/// </para>
/// <para>
/// The arrangement is staged from <c>tools/published-layout.json</c>, the file deploy.ps1 publishes
/// from and both installers lay an install down from. A resolver looking somewhere the layout does
/// not put things fails here, and so does a layout change that a resolver was never taught -- which
/// is the change that would otherwise pass every test and fail on somebody's machine at install time.
/// </para>
/// <para>
/// The files are empty. Nothing here launches anything; every resolver decides on existence and a
/// path, so a zero-byte file answers the question being asked.
/// </para>
/// </summary>
public sealed class PublishedLayoutTests : IDisposable
{
	private static readonly PublishedLayout Layout = PublishedLayout.Load();

	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		$"rosemcp-layout-{Guid.NewGuid():N}");

	/// <summary>
	/// Every component where the layout puts it, a live-app host for every architecture any install
	/// carries, and every XAML provider beside each host.
	/// </summary>
	public PublishedLayoutTests()
	{
		foreach (var component in Layout.Components) Stage(ExecutableOf(component));

		foreach (var host in Layout.AllLiveAppHosts)
		{
			Stage(HostExecutable(host));

			foreach (var provider in Layout.LiveAppHost.XamlProviders.Files) Stage(ProviderBeside(HostFolder(host), host, provider.File));
		}
	}

	/// <summary>
	/// The worker from every folder a broker runs in. The server publishes flat into the install root
	/// and the tray into a folder of its own, so the worker is beside one and a level up from the
	/// other -- and looking only alongside left the tray unable to start a worker at all without
	/// --worker. Invisible from the repository, because the development fallback finds one there
	/// whatever the layout says.
	/// </summary>
	[Test]
	public void Finds_the_worker_from_every_folder_a_broker_runs_in()
	{
		var worker = ExecutableOf(Layout.ComponentFor("RoseMcp.Worker"));

		foreach (var broker in BrokerFolders())
		{
			var resolved = WorkerLauncher.ResolveWorkerPath(new BrokerOptions(), broker, searchRepository: false);

			Path.GetFullPath(resolved).ShouldBe(Path.GetFullPath(worker), $"resolving from {broker}");
		}
	}

	/// <summary>
	/// The inspector is a sibling of the tray's folder, not of the tray's exe, and the tray is what
	/// launches it; a server asks from the install root. Both are resolutions that have to work on an
	/// install and cannot be seen from inside the repository.
	/// </summary>
	[Test]
	public void Finds_the_inspector_from_every_folder_a_broker_runs_in()
	{
		var inspector = ExecutableOf(Layout.ComponentFor("RoseMcp.Inspector"));

		foreach (var broker in BrokerFolders())
		{
			var resolved = InspectorLauncher.ResolvePath(baseDirectory: broker, searchRepository: false);

			resolved.ShouldNotBeNull($"resolving from {broker}");
			Path.GetFullPath(resolved!).ShouldBe(Path.GetFullPath(inspector), $"resolving from {broker}");
		}
	}

	/// <summary>
	/// Null rather than a throw, because an inspector that is not installed is an ordinary state:
	/// the tray labels the menu item instead of offering something that cannot work.
	/// </summary>
	[Test]
	public void Says_nothing_rather_than_failing_when_no_inspector_is_published()
	{
		File.Delete(ExecutableOf(Layout.ComponentFor("RoseMcp.Inspector")));

		foreach (var broker in BrokerFolders())
		{
			InspectorLauncher.ResolvePath(baseDirectory: broker, searchRepository: false).ShouldBeNull($"resolving from {broker}");
		}
	}

	/// <summary>
	/// The command a person copies out of the tray. The exe is quoted because an install path
	/// routinely has a space in it, and the session is only named when there is one to name.
	/// </summary>
	[Test]
	public void Builds_a_command_line_a_shell_can_run()
	{
		var token = new OperatorToken("a-token");

		var withSession = InspectorLauncher.CommandLine(
			@"C:\Program Files\Rose\inspector\RoseMcp.Inspector.exe", "127.0.0.1", 5077, token, "session-abcd1234");

		withSession.ShouldStartWith("\"C:\\Program Files\\Rose\\inspector\\RoseMcp.Inspector.exe\"", Case.Sensitive);
		withSession.ShouldContain("--port 5077", Case.Sensitive);
		withSession.ShouldContain("--token a-token", Case.Sensitive);
		withSession.ShouldContain("--session session-abcd1234", Case.Sensitive);

		var withoutSession = InspectorLauncher.CommandLine(
			@"C:\Rose\RoseMcp.Inspector.exe", "127.0.0.1", 5077, token, sessionId: null);

		withoutSession.ShouldNotContain("--session", Case.Sensitive);
	}

	/// <summary>
	/// The arguments go across as a list rather than a string, so nothing is quoted and nothing can
	/// be mis-split. A token that came back different because a shell re-parsed it would be refused
	/// with no clue why.
	/// </summary>
	[Test]
	public void Passes_arguments_as_a_list()
	{
		var arguments = InspectorLauncher.Arguments("127.0.0.1", 5077, new OperatorToken("a-token"), "session-1");

		arguments.ShouldBe(
			["--host", "127.0.0.1", "--port", "5077", "--token", "a-token", "--session", "session-1"]);

		// The target's pid rides along when the launcher knows it, because the inspector keys its
		// single instance on the process and cannot ask anybody what that is in time.
		InspectorLauncher.Arguments("127.0.0.1", 5077, new OperatorToken("a-token"), "session-1", 4242).ShouldBe(
			["--host", "127.0.0.1", "--port", "5077", "--token", "a-token", "--session", "session-1", "--target-pid", "4242"]);
	}

	/// <summary>
	/// Every live-app host the layout ships, from every folder a broker runs in. The hosts are
	/// published once, into the root, so the tray finds them a level up rather than every install
	/// carrying a second copy.
	/// </summary>
	[Test]
	public void Finds_every_live_app_host_from_every_folder_a_broker_runs_in()
	{
		foreach (var host in Layout.AllLiveAppHosts)
		{
			foreach (var broker in BrokerFolders())
			{
				var resolved = LiveAppHostLauncher.ResolveHostPath(
					ArchitectureOf(host), new BrokerOptions(), broker, searchRepository: false);

				Path.GetFullPath(resolved).ShouldBe(Path.GetFullPath(HostExecutable(host)), $"{host} from {broker}");
			}
		}
	}

	/// <summary>
	/// An architecture the install does not carry is refused with the layout it wanted, rather than
	/// falling back to one that cannot debug the target. ICorDebug has no cross-architecture path,
	/// so the wrong host is not a slower answer but a wrong one.
	/// </summary>
	[Test]
	public void Says_which_layout_it_wanted_when_the_host_is_not_published()
	{
		var host = Layout.AllLiveAppHosts[^1];
		Directory.Delete(HostFolder(host), recursive: true);

		var error = Should.Throw<FileNotFoundException>(
			() => LiveAppHostLauncher.ResolveHostPath(
				ArchitectureOf(host), new BrokerOptions(), _root, searchRepository: false)).ShouldBeOfType<FileNotFoundException>();

		error.Message.ShouldContain(host, Case.Sensitive);
		error.Message.ShouldContain(Layout.LiveAppHost.Folder.Split('/')[0], Case.Sensitive);
	}

	/// <summary>
	/// Every provider beside every host, which is what a deploy produces and the case that has to work
	/// on an install. Inside a checkout the repository fallback always found something, so the suite
	/// could not tell a correct layout from a broken one.
	/// </summary>
	[Test]
	public void Finds_every_xaml_provider_beside_its_host()
	{
		foreach (var host in Layout.AllLiveAppHosts)
		{
			foreach (var provider in Layout.LiveAppHost.XamlProviders.Files)
			{
				var resolved = XamlProviderPath.Resolve(new XamlProviderLookup(
					Configured: null,
					BaseDirectory: HostFolder(host),
					RuntimeIdentifier: host,
					Platform: PlatformOf(host),
					ProviderFileName: provider.File,
					ProviderProjectName: provider.Project));

				resolved.ShouldNotBeNull($"{provider.File} for {host}");
				Path.GetFullPath(resolved!).ShouldBe(
					Path.GetFullPath(ProviderBeside(HostFolder(host), host, provider.File)), $"{provider.File} for {host}");
			}
		}
	}

	/// <summary>
	/// Every install can debug a target of its own architecture. A runtime whose hosts left itself out
	/// would install, start, and refuse the most ordinary target there is.
	/// </summary>
	[Test]
	public void Every_runtime_carries_a_debug_host_for_its_own_architecture()
	{
		foreach (var runtime in Layout.Runtimes)
		{
			runtime.LiveAppHosts.ShouldContain(runtime.Rid);
		}
	}

	/// <summary>
	/// The log root with nothing passed, which every other logging test supplies and so never
	/// exercises -- and it is the branch an install actually takes.
	/// </summary>
	/// <remarks>
	/// The failure it guards is not an exception. GetFolderPath answers with an empty string where
	/// the account has no profile loaded, Path.Combine then yields a relative path, and the logs go
	/// under whatever directory the client started the process in -- so the install looks as though
	/// it never wrote any.
	/// </remarks>
	[Test]
	[Arguments("Server")]
	[Arguments("Worker")]
	[Arguments("Tray")]
	public void Puts_the_logs_under_an_absolute_root_with_no_root_supplied(string component)
	{
		var directory = RoseLogFile.DirectoryFor(component);

		Path.IsPathFullyQualified(directory).ShouldBeTrue($"{directory} is relative, so it lands wherever the client started us.");
		directory.ShouldEndWith(Path.Combine("BinaryVibrance", "RoseMCP", "Logs", component), Case.Sensitive);
	}

	/// <summary>An empty root is the same case arriving from the caller rather than the environment.</summary>
	[Test]
	public void Treats_an_empty_root_the_way_it_treats_a_missing_one()
	{
		Path.IsPathFullyQualified(RoseLogFile.DirectoryFor("Server", string.Empty)).ShouldBeTrue();
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A staged layout in the temp directory is not worth failing a test over.
		}
	}

	/// <summary>
	/// An install whose provider did not ship says so rather than guessing, so the caller can name the
	/// architecture it wanted. A host outside a checkout has no third place to look.
	/// </summary>
	[Test]
	public void Says_nothing_when_no_provider_is_installed()
	{
		XamlProviderPath.Resolve(Lookup(UnstagedHost())).ShouldBeNull();
	}

	/// <summary>
	/// A provider for another architecture is not an answer. Injecting it would load a DLL the target
	/// cannot execute, which fails inside somebody else's process rather than here.
	/// </summary>
	[Test]
	public void Does_not_take_a_provider_built_for_another_architecture()
	{
		Stage(ProviderBeside(UnstagedHost(), "win-arm64", "RoseMcp.Xaml.Uwp.Tap.dll"));

		XamlProviderPath.Resolve(Lookup(UnstagedHost())).ShouldBeNull();
	}

	/// <summary>
	/// The override wins, and only when it names a file that is there. A path pointing at nothing falls
	/// through to the layout rather than failing: it is usually a stale environment variable, and the
	/// install beside the host is still the right answer.
	/// </summary>
	[Test]
	public void Prefers_an_override_that_exists_and_passes_over_one_that_does_not()
	{
		var host = UnstagedHost();
		var beside = ProviderBeside(host, "win-x64", "RoseMcp.Xaml.Uwp.Tap.dll");
		var override_ = Path.Combine(_root, "elsewhere", "RoseMcp.Xaml.Uwp.Tap.dll");

		Stage(beside);
		Stage(override_);

		XamlProviderPath.Resolve(Lookup(host) with { Configured = override_ }).ShouldBe(override_);

		XamlProviderPath.Resolve(Lookup(host) with { Configured = Path.Combine(_root, "gone.dll") }).ShouldBe(beside);
	}

	/// <summary>
	/// A UWP host and a WinUI host look in the same place for different files, so the file name is the
	/// only thing separating them and a lookup that ignored it would inject the wrong tap.
	/// </summary>
	[Test]
	public void Looks_for_the_provider_the_tap_names()
	{
		var host = UnstagedHost();
		Stage(ProviderBeside(host, "win-x64", "RoseMcp.Xaml.WinUi.Tap.dll"));

		XamlProviderPath.Resolve(Lookup(host)).ShouldBeNull();
		XamlProviderPath.Resolve(
			Lookup(host) with { ProviderFileName = "RoseMcp.Xaml.WinUi.Tap.dll" }).ShouldNotBeNull();
	}

	private static void Stage(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, []);
	}

	/// <summary>Where the layout puts a component's executable, under the staged root.</summary>
	private string ExecutableOf(PublishedLayout.Component component) =>
		PublishedLayout.Under(_root, component.Folder, PublishedLayout.OnThisMachine(component.Executable));

	private string HostFolder(string host) =>
		PublishedLayout.Under(_root, PublishedLayout.Expand(Layout.LiveAppHost.Folder, host: host));

	private string HostExecutable(string host) =>
		PublishedLayout.Under(HostFolder(host), PublishedLayout.OnThisMachine(Layout.LiveAppHost.Executable));

	/// <summary>Where the layout puts a provider built for <paramref name="host"/>, relative to a host's folder.</summary>
	private static string ProviderBeside(string hostFolder, string host, string file) =>
		PublishedLayout.Under(hostFolder, PublishedLayout.Expand(Layout.LiveAppHost.XamlProviders.Folder, host: host), file);

	/// <summary>
	/// Every folder a broker runs from, asserted non-empty so a layout that marked none could not pass
	/// every resolution test by asking nothing.
	/// </summary>
	private IReadOnlyList<string> BrokerFolders()
	{
		var folders = Layout.BrokerFolders.Select(folder => PublishedLayout.Under(_root, folder)).ToList();
		folders.ShouldNotBeEmpty("tools/published-layout.json marks no component as hosting the broker.");

		return folders;
	}

	/// <summary>
	/// A host folder with nothing staged in it, outside any checkout, so the repository fallback finds
	/// nothing and only the files a test puts there are being asked about.
	/// </summary>
	private string UnstagedHost() => Path.Combine(_root, "unstaged-host");

	/// <summary>
	/// The architecture a debug host's RID serves. A host the layout ships that no architecture maps
	/// to is a host nothing could ever be routed to, which is worth failing on rather than skipping.
	/// </summary>
	private static TargetArchitecture ArchitectureOf(string rid) => rid switch
	{
		"win-x64" => TargetArchitecture.X64,
		"win-arm64" => TargetArchitecture.Arm64,
		"win-x86" => TargetArchitecture.X86,
		_ => throw new InvalidOperationException(
			$"tools/published-layout.json ships a live-app host for {rid}, which no TargetArchitecture names."),
	};

	private static string PlatformOf(string rid) => rid[(rid.IndexOf('-') + 1)..];

	private static XamlProviderLookup Lookup(string baseDirectory) => new(
		Configured: null,
		BaseDirectory: baseDirectory,
		RuntimeIdentifier: "win-x64",
		Platform: "x64",
		ProviderFileName: "RoseMcp.Xaml.Uwp.Tap.dll",
		ProviderProjectName: "RoseMcp.Xaml.Uwp.Tap");
}
