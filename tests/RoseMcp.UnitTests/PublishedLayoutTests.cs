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
	/// down in tray/, and a live-app host per runtime under live-app/&lt;rid&gt;/.
	/// </summary>
	public PublishedLayoutTests()
	{
		Stage("RoseMcp.Server.exe");
		Stage("RoseMcp.Worker.exe");
		Stage("RoseMcp.Worker");
		Stage(Path.Combine("tray", "RoseMcp.Tray.exe"));
		Stage(Path.Combine("live-app", "win-x64", "RoseMcp.LiveApp.exe"));
		Stage(Path.Combine("live-app", "win-x64", "RoseMcp.LiveApp"));
		Stage(Path.Combine("live-app", "win-arm64", "RoseMcp.LiveApp.exe"));
		Stage(Path.Combine("live-app", "win-arm64", "RoseMcp.LiveApp"));
	}

	/// <summary>
	/// The broker's own directory, which is the case that always worked.
	/// </summary>
	[Fact]
	public void Finds_the_worker_beside_the_broker()
	{
		var resolved = WorkerLauncher.ResolveWorkerPath(new BrokerOptions(), _root, searchRepository: false);

		Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(resolved)), Path.GetFullPath(_root));
	}

	/// <summary>
	/// The tray's, which did not (#101). The tray is published into tray/ so that WinUI's payload
	/// cannot overwrite the server's shared assemblies, which puts the worker one level up rather
	/// than alongside -- and looking only alongside left the tray unable to start a worker at all
	/// without --worker. Invisible from the repository, because the development fallback finds one
	/// there whatever the layout says.
	/// </summary>
	[Fact]
	public void Finds_the_worker_one_level_up_from_the_tray()
	{
		var tray = Path.Combine(_root, "tray");

		var resolved = WorkerLauncher.ResolveWorkerPath(new BrokerOptions(), tray, searchRepository: false);

		Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(resolved)), Path.GetFullPath(_root));
	}

	/// <summary>
	/// The live-app host, from both hosts of the broker and for each architecture published.
	/// </summary>
	[Theory]
	[InlineData(TargetArchitecture.X64, "win-x64")]
	[InlineData(TargetArchitecture.Arm64, "win-arm64")]
	public void Finds_the_live_app_host_from_either_broker_host(TargetArchitecture architecture, string rid)
	{
		foreach (var directory in new[] { _root, Path.Combine(_root, "tray") })
		{
			var resolved = LiveAppHostLauncher.ResolveHostPath(
				architecture, new BrokerOptions(), directory, searchRepository: false);

			Assert.Equal(
				Path.GetFullPath(Path.Combine(_root, "live-app", rid)),
				Path.GetDirectoryName(Path.GetFullPath(resolved)));
		}
	}

	/// <summary>
	/// An architecture the install does not carry is refused with the layout it wanted, rather than
	/// falling back to one that cannot debug the target. ICorDebug has no cross-architecture path,
	/// so the wrong host is not a slower answer but a wrong one.
	/// </summary>
	[Fact]
	public void Says_which_layout_it_wanted_when_the_host_is_not_published()
	{
		var error = Assert.Throws<FileNotFoundException>(
			() => LiveAppHostLauncher.ResolveHostPath(
				TargetArchitecture.X86, new BrokerOptions(), _root, searchRepository: false));

		Assert.Contains("win-x86", error.Message, StringComparison.Ordinal);
		Assert.Contains("live-app", error.Message, StringComparison.Ordinal);
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
	[Theory]
	[InlineData("Server")]
	[InlineData("Worker")]
	[InlineData("Tray")]
	public void Puts_the_logs_under_an_absolute_root_with_no_root_supplied(string component)
	{
		var directory = RoseLogFile.DirectoryFor(component);

		Assert.True(Path.IsPathFullyQualified(directory), $"{directory} is relative, so it lands wherever the client started us.");
		Assert.EndsWith(Path.Combine("BinaryVibrance", "RoseMCP", "Logs", component), directory, StringComparison.Ordinal);
	}

	/// <summary>An empty root is the same case arriving from the caller rather than the environment.</summary>
	[Fact]
	public void Treats_an_empty_root_the_way_it_treats_a_missing_one()
	{
		Assert.True(Path.IsPathFullyQualified(RoseLogFile.DirectoryFor("Server", string.Empty)));
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
}
