using System.Runtime.InteropServices;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which live-app host a broker running from source picks out of the repository's build output.
/// <para>
/// The failure this guards against is the worst shape available: an older host answers, every call
/// that exists still works, and only the calls a change added come back as unknown tools -- which
/// reads as the tools being wrong rather than the binary being from before the change. There is
/// nothing in a run to say the process under test was stale.
/// </para>
/// <para>
/// The architecture is left unknown on purpose, which makes the wanted runtime identifier the one
/// this machine runs. That is what a RID-less build is, so the test stages the same pair of
/// candidates whichever machine runs it.
/// </para>
/// </summary>
public sealed class RepositoryHostBuildTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"rosemcp-repo-{Guid.NewGuid():N}");

	/// <summary>
	/// A repository with both shapes of host build in it: the per-RID output a deploy leaves behind,
	/// and the plain one an ordinary build writes.
	/// </summary>
	public RepositoryHostBuildTests()
	{
		File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(_root).FullName, "RoseMcp.slnx"), []);
		Directory.CreateDirectory(BrokerDirectory);
	}

	/// <summary>Where the broker's own build output sits, which is what says which repository and configuration this is.</summary>
	private string BrokerDirectory =>
		Path.Combine(_root, "src", "RoseMcp.Broker", "bin", "Debug", "net10.0");

	[Test]
	public void An_ordinary_build_wins_over_an_earlier_per_rid_one()
	{
		var perRid = StageHost(RuntimeInformation.RuntimeIdentifier, DateTime.UtcNow.AddHours(-3));
		var plain = StageHost(null, DateTime.UtcNow);

		Assert.Equal(plain, Resolve());
		Assert.True(File.Exists(perRid), "the per-RID build is still there and was simply not chosen");
	}

	/// <summary>
	/// And the other way round, so the rule is freshness rather than a new preference for the plain
	/// build: a deploy publishes per RID, and the host it just wrote is the current one.
	/// </summary>
	[Test]
	public void A_fresh_per_rid_build_wins_over_an_earlier_ordinary_one()
	{
		StageHost(null, DateTime.UtcNow.AddHours(-3));
		var perRid = StageHost(RuntimeInformation.RuntimeIdentifier, DateTime.UtcNow);

		Assert.Equal(perRid, Resolve());
	}

	/// <summary>
	/// A build for another architecture is never the answer, however new it is. ICorDebug has no
	/// cross-architecture path, so the wrong host is a wrong answer rather than a slow one.
	/// </summary>
	[Test]
	public void A_host_for_another_architecture_is_not_used()
	{
		var foreign = RuntimeInformation.RuntimeIdentifier == "win-x64" ? "win-arm64" : "win-x64";
		StageHost(foreign, DateTime.UtcNow);

		Assert.Throws<FileNotFoundException>(Resolve);
	}

	/// <summary>
	/// A Release publish left in bin does not answer for a Debug broker, however new it is. That is
	/// the rule this one is layered on, and it has to keep holding.
	/// </summary>
	[Test]
	public void A_build_of_another_configuration_is_not_used()
	{
		StageHost(RuntimeInformation.RuntimeIdentifier, DateTime.UtcNow, configuration: "Release");
		var debug = StageHost(RuntimeInformation.RuntimeIdentifier, DateTime.UtcNow.AddHours(-3));

		Assert.Equal(debug, Resolve());
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A temp directory left behind is litter rather than a failure.
		}
	}

	private string Resolve() => LiveAppHostLauncher.ResolveHostPath(
		TargetArchitecture.Unknown, new BrokerOptions(), BrokerDirectory, searchRepository: true);

	/// <summary>
	/// Writes a host build into the repository, per RID or not, stamped so the test can say which one
	/// somebody built last.
	/// </summary>
	private string StageHost(string? rid, DateTime writtenUtc, string configuration = "Debug")
	{
		var directory = Path.Combine(_root, "src", "RoseMcp.LiveApp", "bin", configuration, "net10.0-windows");
		if (rid is not null) directory = Path.Combine(directory, rid);

		Directory.CreateDirectory(directory);

		var path = Path.Combine(directory, LiveAppHostLauncher.ExecutableName);
		File.WriteAllBytes(path, []);
		File.SetLastWriteTimeUtc(path, writtenUtc);

		return path;
	}
}
