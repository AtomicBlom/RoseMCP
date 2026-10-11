using System.Reflection;
using System.Reflection.Emit;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The version and build each host tells its client during <c>initialize</c>, and what the parent
/// makes of it.
/// <para>
/// A number that never changes reads as an answer, so a bug report naming it names no commit and
/// nobody can tell a stale install from a current one. And a version alone is not enough either: two
/// local builds of different code share one, so what is compared is the commit.
/// </para>
/// </summary>
public sealed class HostVersionTests
{
	/// <summary>
	/// MinVer stamps every assembly here, so a version that is neither the tag nor MinVer's own
	/// no-git fallback means the attribute is missing and the host is reporting a guess.
	/// </summary>
	[Test]
	public void Reads_the_version_minver_stamped()
	{
		var version = HostVersion.Of(typeof(HostVersion).Assembly);

		version.ShouldNotBe("0.0.0");
		version.ShouldNotBe("0.1.0");
		version.ShouldNotContain("+", Case.Sensitive);
	}

	/// <summary>
	/// The build metadata after '+' is the commit. It belongs to the build identity a handshake sends,
	/// and a version an update check compares against a release tag must not carry it.
	/// </summary>
	[Test]
	public void Drops_the_commit_hash()
	{
		var stamped = typeof(HostVersion).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
			.InformationalVersion;

		HostVersion.Of(typeof(HostVersion).Assembly).ShouldBe(stamped.Split('+')[0]);
	}

	/// <summary>
	/// The build a child reports is compared against the parent's, which is what makes computing it
	/// worth doing at all. A child from a stale <c>bin</c> otherwise answers as whatever it is and the
	/// mismatch surfaces as a missing field or an unknown tool.
	/// </summary>
	[Test]
	public void A_child_of_the_same_build_is_not_worth_saying_anything_about()
	{
		var same = BuildIdentity.Of(typeof(WorkspaceManager).Assembly).ToHandshake();

		ChildHostVersion.Mismatch(same, @"C:\rose\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly).ShouldBeNull();
	}

	/// <summary>
	/// And a different one is reported with both builds and the path it came from, saying the child's
	/// commit is unknown rather than leaving it out. The path is the actionable half: the cause is
	/// nearly always a stale build output or an environment variable pointing at one, and neither is
	/// visible from the symptom.
	/// </summary>
	[Test]
	public void A_child_of_another_build_is_named_with_both_versions_and_its_path()
	{
		var mismatch = ChildHostVersion.Mismatch(
			"0.4.0", @"C:\rose\bin\Release\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly);

		mismatch.ShouldNotBeNull();
		mismatch!.ShouldContain("0.4.0, commit unknown", Case.Sensitive);
		mismatch.ShouldContain(BuildIdentity.Of(typeof(WorkspaceManager).Assembly).Describe(), Case.Sensitive);
		mismatch.ShouldContain(@"C:\rose\bin\Release\RoseMcp.Worker.exe", Case.Sensitive);
	}

	/// <summary>
	/// Two local builds at the same height above a tag share a version, so a version compare passes
	/// a worker built from other code. The commit is what is compared, and both are named.
	/// </summary>
	[Test]
	public void A_child_of_another_commit_is_named_with_both_commits_though_the_version_matches()
	{
		var parent = BuildIdentity.Of(typeof(WorkspaceManager).Assembly);
		var other = parent with { Commit = new string('b', 40), Dirty = false };

		var mismatch = ChildHostVersion.Mismatch(
			other.ToHandshake(), @"C:\rose\bin\Debug\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly);

		mismatch.ShouldNotBeNull();
		mismatch!.ShouldContain($"{parent.Version} at bbbbbbb", Case.Sensitive);
		mismatch.ShouldContain(parent.Describe(), Case.Sensitive);
		mismatch.ShouldContain(@"C:\rose\bin\Debug\RoseMcp.Worker.exe", Case.Sensitive);
	}

	/// <summary>
	/// A child from before commits were sent reports a bare version. Where it is this version there is
	/// nothing to go on but that, and calling every older child a mismatch would teach a reader to
	/// ignore the warning.
	/// </summary>
	[Test]
	public void A_child_that_sends_no_commit_is_compared_by_version()
	{
		var parent = BuildIdentity.Of(typeof(WorkspaceManager).Assembly);

		ChildHostVersion.Mismatch(parent.Version, @"C:\rose\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly).ShouldBeNull();
	}

	/// <summary>
	/// A child that reports nothing is as much a stranger as one reporting a different number, and
	/// silence is the easier of the two to misread as agreement.
	/// </summary>
	[Test]
	public void A_child_that_reports_no_version_is_not_taken_for_a_match()
	{
		ChildHostVersion.Mismatch(null, @"C:\rose\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly).ShouldNotBeNull();
		ChildHostVersion.Mismatch("  ", @"C:\rose\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly).ShouldNotBeNull();
	}

	/// <summary>
	/// And that both hops actually ask. A pure comparison nothing calls is the very shape this card
	/// exists to close, so the two places that launch a child are checked for the call by name.
	/// </summary>
	[Test]
	public void Both_hops_that_launch_a_child_compare_its_version()
	{
		foreach (var file in new[] { "WorkspaceWorker.cs", "LiveAppSession.cs" })
		{
			var source = File.ReadAllText(Path.Combine(BrokerSource(), file));

			source.ShouldContain("ChildHostVersion.Mismatch", Case.Sensitive);
			source.ShouldContain("ServerInfo?.Version", Case.Sensitive);
		}
	}

	/// <summary>
	/// An assembly MinVer never touched says so rather than inventing a number, which is the shape a
	/// build from an archive with no <c>.git</c> would take.
	/// <para>
	/// A dynamic assembly, because every assembly in this repository is stamped -- this test project
	/// included, which is what makes a stand-in type here the wrong way to ask the question.
	/// </para>
	/// </summary>
	[Test]
	public void Says_it_does_not_know_rather_than_guessing()
	{
		var unstamped = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName("RoseMcp.NothingStampedThis"),
			AssemblyBuilderAccess.Run);

		HostVersion.Of(unstamped).ShouldBe("0.0.0");
	}

	/// <summary>
	/// The broker's own sources, found by walking up to the solution rather than copied into the
	/// build output, so the test reads what a person edits.
	/// </summary>
	private static string BrokerSource()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
			{
				return Path.Combine(directory.FullName, "src", "RoseMcp.Broker");
			}
		}

		throw new InvalidOperationException($"No RoseMcp.slnx above {AppContext.BaseDirectory}.");
	}
}
