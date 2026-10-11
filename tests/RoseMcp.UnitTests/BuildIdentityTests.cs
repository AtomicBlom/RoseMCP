using System.Reflection;
using System.Reflection.Emit;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which build a binary is, as stamped by the build and as sent in a handshake.
/// <para>
/// The stamp is read back from this test assembly, which the same <c>Directory.Build.props</c>
/// stamps as every host: a stamp that quietly stopped happening would leave every handshake
/// comparing versions again, and two local builds of different code would pass as one.
/// </para>
/// </summary>
public sealed class BuildIdentityTests
{
	private const string Commit = "c11d75a16a82274f8c52a9e33110afee4bdecc0a";

	private static readonly BuildIdentity Stamped = BuildIdentity.Of(typeof(BuildIdentityTests).Assembly);

	/// <summary>The commit, in full, matching the one the SDK put in the informational version.</summary>
	[Test]
	public void Every_assembly_carries_its_full_commit()
	{
		var informational = typeof(BuildIdentityTests).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
			.InformationalVersion;

		Stamped.Commit.ShouldNotBeNull();
		Stamped.Commit!.Length.ShouldBe(40);
		informational.ShouldContain(Stamped.Commit, Case.Insensitive);
	}

	/// <summary>
	/// Whether the tree was dirty is known wherever git could be asked, which is every build of this
	/// repository; null would mean the stamp ran no git at all.
	/// </summary>
	[Test]
	public void Every_assembly_says_whether_its_tree_was_dirty()
	{
		Stamped.Dirty.ShouldNotBeNull();
	}

	/// <summary>
	/// The time it was compiled, which is the compile of this test assembly and so cannot be later
	/// than the test run reading it.
	/// </summary>
	[Test]
	public void Every_assembly_carries_its_build_time()
	{
		Stamped.BuiltUtc.ShouldNotBeNull();
		Stamped.BuiltUtc!.Value.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(1));
		Stamped.BuiltUtc.Value.ShouldBeGreaterThan(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
	}

	/// <summary>
	/// A local build names the checkout it was made in, which is the one holding the solution these
	/// tests were built from. A CI build names none: its runner's path means nothing anywhere else.
	/// </summary>
	[Test]
	public void A_local_build_names_its_checkout()
	{
		var onCi = Environment.GetEnvironmentVariable("CI") == "true"
			|| Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

		if (onCi)
		{
			Stamped.Checkout.ShouldBeNull();
			return;
		}

		Stamped.Checkout.ShouldNotBeNull();
		File.Exists(Path.Combine(Stamped.Checkout!, "RoseMcp.slnx")).ShouldBeTrue(Stamped.Checkout);
	}

	/// <summary>
	/// The handshake carries the commit as SemVer build metadata, and reads back as the same build,
	/// dirty flag included.
	/// </summary>
	[Test]
	public void A_handshake_reads_back_as_the_build_that_sent_it()
	{
		var clean = new BuildIdentity { Version = "1.3.0-rc.1.2", Commit = Commit, Dirty = false };
		var dirty = clean with { Dirty = true };

		clean.ToHandshake().ShouldBe($"1.3.0-rc.1.2+{Commit}");
		dirty.ToHandshake().ShouldBe($"1.3.0-rc.1.2+{Commit}.dirty");

		BuildIdentity.FromHandshake(clean.ToHandshake()).ShouldBe(clean);
		BuildIdentity.FromHandshake(dirty.ToHandshake()).ShouldBe(dirty);
	}

	/// <summary>
	/// A host from before the commit was sent reports a bare version, which is a build whose commit
	/// is unknown rather than a malformed one; nothing at all reads as not knowing either.
	/// </summary>
	[Test]
	public void A_bare_version_is_a_build_with_no_commit()
	{
		BuildIdentity.FromHandshake("0.4.0").ShouldBe(new BuildIdentity { Version = "0.4.0" });
		BuildIdentity.FromHandshake(null).ShouldBe(new BuildIdentity { Version = "0.0.0" });
		BuildIdentity.FromHandshake("  ").ShouldBe(new BuildIdentity { Version = "0.0.0" });
	}

	/// <summary>A SHA-256 repository's commits are sixty-four digits, and are commits all the same.</summary>
	[Test]
	public void A_sha256_commit_is_read_as_a_commit()
	{
		var long64 = new string('d', 64);

		BuildIdentity.FromHandshake($"1.0.0+{long64}").Commit.ShouldBe(long64);
		BuildIdentity.FromHandshake("1.0.0+notacommit").Commit.ShouldBeNull();
	}

	/// <summary>
	/// The commit decides where both sides have one, whatever the versions say; the version decides
	/// only where one side has no commit.
	/// </summary>
	[Test]
	public void Commits_decide_and_versions_only_stand_in_for_them()
	{
		var ours = new BuildIdentity { Version = "1.3.0", Commit = Commit, Dirty = false };

		ours.IsSameBuildAs(ours with { Commit = new string('e', 40) }).ShouldBeFalse();
		ours.IsSameBuildAs(ours with { Version = "1.3.1" }).ShouldBeTrue();
		ours.IsSameBuildAs(new BuildIdentity { Version = "1.3.0" }).ShouldBeTrue();
		ours.IsSameBuildAs(new BuildIdentity { Version = "1.2.0" }).ShouldBeFalse();
	}

	/// <summary>
	/// The dirty flag is said, not compared. It is stamped when an assembly compiles, so a worker
	/// untouched since the last commit says clean beside a broker rebuilt from an edit, and comparing
	/// them would warn on every edit a person builds.
	/// </summary>
	[Test]
	public void A_dirty_flag_is_said_and_not_compared()
	{
		var clean = new BuildIdentity { Version = "1.3.0", Commit = Commit, Dirty = false };
		var dirty = clean with { Dirty = true };

		clean.IsSameBuildAs(dirty).ShouldBeTrue();
		dirty.Describe().ShouldBe("1.3.0 at c11d75a with uncommitted changes");
	}

	/// <summary>A mismatch names both builds, each by its short commit, and which is which.</summary>
	[Test]
	public void A_mismatch_names_both_commits()
	{
		var ours = new BuildIdentity { Version = "1.3.0", Commit = Commit, Dirty = true };
		var theirs = ours with { Commit = new string('f', 40), Dirty = false };

		BuildIdentity.Mismatch("The broker", theirs, ours)
			.ShouldBe("The broker is build 1.3.0 at fffffff, where this one is 1.3.0 at c11d75a with uncommitted changes.");
		BuildIdentity.Mismatch("The broker", ours, ours).ShouldBeNull();
		BuildIdentity.Mismatch("The broker", new BuildIdentity { Version = "1.2.0" }, ours)
			.ShouldBe("The broker is build 1.2.0, commit unknown, where this one is 1.3.0 at c11d75a with uncommitted changes.");
	}

	/// <summary>
	/// An assembly nothing stamped says so: no commit, no time, no checkout, and the version that is
	/// honest about not knowing. A dynamic assembly, because every assembly built here is stamped.
	/// </summary>
	[Test]
	public void An_unstamped_assembly_knows_nothing_and_says_so()
	{
		var unstamped = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName("RoseMcp.NothingStampedThis"),
			AssemblyBuilderAccess.Run);

		BuildIdentity.Of(unstamped).ShouldBe(new BuildIdentity { Version = "0.0.0" });
	}
}
