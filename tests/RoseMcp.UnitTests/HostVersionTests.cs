using System.Reflection;
using System.Reflection.Emit;

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

		Assert.NotEqual("0.0.0", version);
		Assert.NotEqual("0.1.0", version);
		Assert.DoesNotContain("+", version, StringComparison.Ordinal);
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

		Assert.Equal(stamped.Split('+')[0], HostVersion.Of(typeof(HostVersion).Assembly));
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

		Assert.Equal("0.0.0", HostVersion.Of(unstamped));
	}
}
