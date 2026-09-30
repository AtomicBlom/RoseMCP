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
/// switched off here and the layout is staged by hand, which is the whole point: what is being
/// tested is the arrangement, not the machine this runs on.
/// </para>
/// <para>
/// The files are empty. Nothing here launches anything; every resolver decides on existence and a
/// path, so a zero-byte file answers the question being asked.
/// </para>
/// </summary>
public sealed class PublishedLayoutTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		$"rosemcp-layout-{Guid.NewGuid():N}");

	/// <summary>
	/// The shape <c>Publish-Tree</c> writes: the broker flat in the install root, the tray one level
	/// down in tray/, the inspector beside it in inspector/, and a live-app host per runtime under
	/// live-app/&lt;rid&gt;/.
	/// </summary>
	public PublishedLayoutTests()
	{
		Stage("RoseMcp.Server.exe");
		Stage("RoseMcp.Worker.exe");
		Stage("RoseMcp.Worker");
		Stage(Path.Combine("tray", "RoseMcp.Tray.exe"));
		Stage(Path.Combine("inspector", InspectorLauncher.ExecutableName));
		Stage(Path.Combine("live-app", "win-x64", "RoseMcp.LiveApp.exe"));
		Stage(Path.Combine("live-app", "win-x64", "RoseMcp.LiveApp"));
		Stage(Path.Combine("live-app", "win-arm64", "RoseMcp.LiveApp.exe"));
		Stage(Path.Combine("live-app", "win-arm64", "RoseMcp.LiveApp"));
	}

	/// <summary>
	/// The broker's own directory, which is the case that always worked.
	/// </summary>
	[Test]
	public void Finds_the_worker_beside_the_broker()
	{
		var resolved = WorkerLauncher.ResolveWorkerPath(new BrokerOptions(), _root, searchRepository: false);

		Path.GetFullPath(_root).ShouldBe(Path.GetDirectoryName(Path.GetFullPath(resolved)));
	}

	/// <summary>
	/// The tray's, which did not (#101). The tray is published into tray/ so that WinUI's payload
	/// cannot overwrite the server's shared assemblies, which puts the worker one level up rather
	/// than alongside -- and looking only alongside left the tray unable to start a worker at all
	/// without --worker. Invisible from the repository, because the development fallback finds one
	/// there whatever the layout says.
	/// </summary>
	[Test]
	public void Finds_the_worker_one_level_up_from_the_tray()
	{
		var tray = Path.Combine(_root, "tray");

		var resolved = WorkerLauncher.ResolveWorkerPath(new BrokerOptions(), tray, searchRepository: false);

		Path.GetFullPath(_root).ShouldBe(Path.GetDirectoryName(Path.GetFullPath(resolved)));
	}

	/// <summary>
	/// The inspector is a sibling of the tray's folder, not of the tray's exe, and the tray is what
	/// launches it. That is the resolution that has to work on an install and cannot be seen from
	/// inside the repository, where the development fallback answers whatever the layout says.
	/// </summary>
	[Test]
	public void Finds_the_inspector_beside_the_trays_own_folder()
	{
		var tray = Path.Combine(_root, "tray");

		var resolved = InspectorLauncher.ResolvePath(baseDirectory: tray, searchRepository: false);

		resolved.ShouldNotBeNull();
		Path.GetFullPath(resolved!).ShouldBe(
			Path.GetFullPath(Path.Combine(_root, "inspector", InspectorLauncher.ExecutableName)));
	}

	/// <summary>
	/// And from the install root, which is where a server rather than a tray would be asking.
	/// </summary>
	[Test]
	public void Finds_the_inspector_from_the_install_root()
	{
		var resolved = InspectorLauncher.ResolvePath(baseDirectory: _root, searchRepository: false);

		resolved.ShouldNotBeNull();
		Path.GetFullPath(resolved!).ShouldBe(
			Path.GetFullPath(Path.Combine(_root, "inspector", InspectorLauncher.ExecutableName)));
	}

	/// <summary>
	/// Null rather than a throw, because an inspector that is not installed is an ordinary state:
	/// the tray labels the menu item instead of offering something that cannot work.
	/// </summary>
	[Test]
	public void Says_nothing_rather_than_failing_when_no_inspector_is_published()
	{
		var elsewhere = Path.Combine(_root, "live-app", "win-x64");

		var resolved = InspectorLauncher.ResolvePath(baseDirectory: elsewhere, searchRepository: false);

		resolved.ShouldBeNull();
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
	/// The live-app host, from both hosts of the broker and for each architecture published.
	/// </summary>
	[Test]
	[Arguments(TargetArchitecture.X64, "win-x64")]
	[Arguments(TargetArchitecture.Arm64, "win-arm64")]
	public void Finds_the_live_app_host_from_either_broker_host(TargetArchitecture architecture, string rid)
	{
		foreach (var directory in new[] { _root, Path.Combine(_root, "tray") })
		{
			var resolved = LiveAppHostLauncher.ResolveHostPath(
				architecture, new BrokerOptions(), directory, searchRepository: false);

			Path.GetDirectoryName(Path.GetFullPath(resolved)).ShouldBe(
				Path.GetFullPath(Path.Combine(_root, "live-app", rid)));
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
		var error = Should.Throw<FileNotFoundException>(
			() => LiveAppHostLauncher.ResolveHostPath(
				TargetArchitecture.X86, new BrokerOptions(), _root, searchRepository: false)).ShouldBeOfType<FileNotFoundException>();

		error.Message.ShouldContain("win-x86", Case.Sensitive);
		error.Message.ShouldContain("live-app", Case.Sensitive);
	}

	/// <summary>
	/// The log root with nothing passed, which every other logging test supplies and so never
	/// exercises -- and it is the branch an install actually takes (#111).
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

	private void Stage(string relativePath)
	{
		var path = Path.Combine(_root, relativePath);

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, []);
	}

	/// <summary>
	/// The provider beside the host, which is what a deploy produces:
	/// <c>live-app/&lt;rid&gt;/xaml-provider/&lt;rid&gt;/</c>. This is the case that has to work on an
	/// install, and the one the repository fallback used to hide -- inside a checkout it always found
	/// something, so the suite could not tell a correct layout from a broken one.
	/// </summary>
	[Test]
	public void Finds_the_xaml_provider_beside_the_live_app_host()
	{
		Stage(Path.Combine("live-app", "win-x64", "xaml-provider", "win-x64", "RoseMcp.Xaml.Uwp.Tap.dll"));

		var resolved = XamlProviderPath.Resolve(Lookup(Path.Combine(_root, "live-app", "win-x64")));

		resolved.ShouldBe(
			Path.Combine(_root, "live-app", "win-x64", "xaml-provider", "win-x64", "RoseMcp.Xaml.Uwp.Tap.dll"));
	}

	/// <summary>
	/// An install whose provider did not ship says so rather than guessing, so the caller can name the
	/// architecture it wanted. A host outside a checkout has no third place to look.
	/// </summary>
	[Test]
	public void Says_nothing_when_no_provider_is_installed()
	{
		XamlProviderPath.Resolve(Lookup(Path.Combine(_root, "live-app", "win-x64"))).ShouldBeNull();
	}

	/// <summary>
	/// A provider for another architecture is not an answer. Injecting it would load a DLL the target
	/// cannot execute, which fails inside somebody else's process rather than here.
	/// </summary>
	[Test]
	public void Does_not_take_a_provider_built_for_another_architecture()
	{
		Stage(Path.Combine("live-app", "win-x64", "xaml-provider", "win-arm64", "RoseMcp.Xaml.Uwp.Tap.dll"));

		XamlProviderPath.Resolve(Lookup(Path.Combine(_root, "live-app", "win-x64"))).ShouldBeNull();
	}

	/// <summary>
	/// The override wins, and only when it names a file that is there. A path pointing at nothing falls
	/// through to the layout rather than failing: it is usually a stale environment variable, and the
	/// install beside the host is still the right answer.
	/// </summary>
	[Test]
	public void Prefers_an_override_that_exists_and_passes_over_one_that_does_not()
	{
		Stage(Path.Combine("live-app", "win-x64", "xaml-provider", "win-x64", "RoseMcp.Xaml.Uwp.Tap.dll"));
		Stage(Path.Combine("elsewhere", "RoseMcp.Xaml.Uwp.Tap.dll"));

		var host = Path.Combine(_root, "live-app", "win-x64");
		var override_ = Path.Combine(_root, "elsewhere", "RoseMcp.Xaml.Uwp.Tap.dll");

		XamlProviderPath.Resolve(Lookup(host) with { Configured = override_ }).ShouldBe(override_);

		XamlProviderPath.Resolve(Lookup(host) with { Configured = Path.Combine(_root, "gone.dll") }).ShouldBe(
			Path.Combine(host, "xaml-provider", "win-x64", "RoseMcp.Xaml.Uwp.Tap.dll"));
	}

	/// <summary>
	/// A UWP host and a WinUI host look in the same place for different files, so the file name is the
	/// only thing separating them and a lookup that ignored it would inject the wrong tap.
	/// </summary>
	[Test]
	public void Looks_for_the_provider_the_tap_names()
	{
		Stage(Path.Combine("live-app", "win-x64", "xaml-provider", "win-x64", "RoseMcp.Xaml.WinUi.Tap.dll"));

		var host = Path.Combine(_root, "live-app", "win-x64");

		XamlProviderPath.Resolve(Lookup(host)).ShouldBeNull();
		XamlProviderPath.Resolve(
			Lookup(host) with { ProviderFileName = "RoseMcp.Xaml.WinUi.Tap.dll" }).ShouldNotBeNull();
	}

	/// <summary>
	/// A staged host, which is outside any checkout, so the repository fallback finds nothing and the
	/// published layout is the only thing being asked about.
	/// </summary>
	private static XamlProviderLookup Lookup(string baseDirectory) => new(
		Configured: null,
		BaseDirectory: baseDirectory,
		RuntimeIdentifier: "win-x64",
		Platform: "x64",
		ProviderFileName: "RoseMcp.Xaml.Uwp.Tap.dll",
		ProviderProjectName: "RoseMcp.Xaml.Uwp.Tap");
}
