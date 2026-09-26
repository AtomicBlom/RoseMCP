using System.Reflection;
using System.Reflection.Emit;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The version each host tells its client during <c>initialize</c>.
/// <para>
/// Three of the four reported <c>0.1.0</c> for every build ever made, which is worse than saying
/// nothing: a number that never changes reads as an answer, so a bug report naming it names no
/// commit and nobody can tell a stale install from a current one.
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
	/// The build metadata after '+' is the commit hash. It belongs in a log rather than in a
	/// handshake, and dropping it is what keeps the reported version comparable.
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
	/// The version a child reports is compared against the parent's, which is what makes computing
	/// it worth doing at all. Four hosts reported one and nothing read any of them, so a child from
	/// a stale <c>bin</c> answered as whatever it was and the mismatch surfaced as a missing field
	/// or an unknown tool.
	/// </summary>
	[Test]
	public void A_child_of_the_same_build_is_not_worth_saying_anything_about()
	{
		var same = HostVersion.Of(typeof(WorkspaceManager).Assembly);

		ChildHostVersion.Mismatch(same, @"C:\rose\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly).ShouldBeNull();
	}

	/// <summary>
	/// And a different one is reported with both numbers and the path it came from. The path is the
	/// actionable half: the cause is nearly always a stale build output or an environment variable
	/// pointing at one, and neither is visible from the symptom.
	/// </summary>
	[Test]
	public void A_child_of_another_build_is_named_with_both_versions_and_its_path()
	{
		var mismatch = ChildHostVersion.Mismatch(
			"0.4.0", @"C:\rose\bin\Release\RoseMcp.Worker.exe", typeof(WorkspaceManager).Assembly);

		mismatch.ShouldNotBeNull();
		mismatch!.ShouldContain("0.4.0", Case.Sensitive);
		mismatch.ShouldContain(HostVersion.Of(typeof(WorkspaceManager).Assembly), Case.Sensitive);
		mismatch.ShouldContain(@"C:\rose\bin\Release\RoseMcp.Worker.exe", Case.Sensitive);
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
