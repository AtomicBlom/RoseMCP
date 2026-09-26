using RoseMcp.Ui.Core.Updates;

namespace RoseMcp.UnitTests;

/// <summary>
/// The comparison the update check turns on.
/// <para>
/// Every case here is one where getting it wrong produces a wrong answer rather than an error:
/// either an upgrade that is never offered, or one offered forever. A plain <c>Version</c> comparison
/// gets several of them wrong, which is why this type exists at all.
/// </para>
/// </summary>
public sealed class ReleaseVersionTests
{
	[Test]
	[Arguments("v0.3.0", 0, 3, 0)]
	[Arguments("0.3.0", 0, 3, 0)]
	[Arguments("V1.2.3", 1, 2, 3)]
	[Arguments("1.2", 1, 2, 0)]
	[Arguments("2", 2, 0, 0)]
	public void Parses_a_tag_with_or_without_its_prefix(string text, int major, int minor, int patch)
	{
		ReleaseVersion.TryParse(text, out var version).ShouldBeTrue();
		version.Major.ShouldBe(major);
		version.Minor.ShouldBe(minor);
		version.Patch.ShouldBe(patch);
	}

	/// <summary>
	/// MinVer stamps the commit after '+'. Semver says build metadata takes no part in precedence,
	/// and comparing it here would report every rebuild as an upgrade.
	/// </summary>
	[Test]
	public void Discards_build_metadata_because_it_takes_no_part_in_precedence()
	{
		ReleaseVersion.TryParse("1.1.1+1a2b3c4", out var stamped).ShouldBeTrue();
		ReleaseVersion.TryParse("1.1.1", out var plain).ShouldBeTrue();

		ReleaseVersion.Compare(stamped, plain).ShouldBe(0);
	}

	[Test]
	[Arguments("")]
	[Arguments("   ")]
	[Arguments("not-a-version")]
	[Arguments("1.2.3.4")]
	[Arguments("1.-2.3")]
	[Arguments("nightly")]
	public void Refuses_what_it_cannot_compare(string text)
	{
		ReleaseVersion.TryParse(text, out _).ShouldBeFalse();
	}

	[Test]
	[Arguments("0.3.0", "0.4.0")]
	[Arguments("0.3.0", "0.3.1")]
	[Arguments("0.3.0", "1.0.0")]
	[Arguments("0.9.9", "1.0.0")]
	public void Orders_by_number_before_anything_else(string older, string newer)
	{
		Older(older, newer).ShouldBeTrue();
		Older(newer, older).ShouldBeFalse();
	}

	/// <summary>
	/// The case the whole type exists for. A <c>Version</c> comparison calls these equal, so somebody
	/// running rc.1 is never offered the release it was a candidate for.
	/// </summary>
	[Test]
	public void A_release_outranks_its_own_release_candidate()
	{
		Older("0.3.0-rc.1", "0.3.0").ShouldBeTrue();
		Older("0.3.0", "0.3.0-rc.1").ShouldBeFalse();
	}

	[Test]
	[Arguments("0.3.0-rc.1", "0.3.0-rc.2")]
	[Arguments("0.3.0-alpha", "0.3.0-beta")]
	[Arguments("0.3.0-alpha.1", "0.3.0-alpha.2")]
	[Arguments("0.3.0-rc.1", "0.3.0-rc.1.1")]
	public void Orders_prereleases_among_themselves(string older, string newer)
	{
		Older(older, newer).ShouldBeTrue();
		Older(newer, older).ShouldBeFalse();
	}

	/// <summary>Semver's own rule, and not what sorting the identifiers as text would do.</summary>
	[Test]
	public void A_numeric_identifier_ranks_below_an_alphanumeric_one()
	{
		Older("1.0.0-alpha.1", "1.0.0-alpha.beta").ShouldBeTrue();
	}

	/// <summary>"10" sorts before "9" as text, which would stop offering rc.10 to anybody on rc.9.</summary>
	[Test]
	public void Numeric_identifiers_compare_as_numbers_rather_than_text()
	{
		Older("1.0.0-rc.9", "1.0.0-rc.10").ShouldBeTrue();
	}

	[Test]
	public void The_same_version_is_not_an_upgrade()
	{
		Older("1.2.3", "1.2.3").ShouldBeFalse();
		Older("1.2.3-rc.1", "1.2.3-rc.1").ShouldBeFalse();
	}

	/// <summary>
	/// Why the check is off in a build from source. MinVer's height makes a working tree a prerelease
	/// of the <em>next</em> patch, so it reads as older than that patch however new the code is -- and
	/// a developer would be told to upgrade to the version they are sitting on top of.
	/// </summary>
	[Test]
	public void A_development_build_reads_as_older_than_the_release_it_anticipates()
	{
		Older("1.1.1-alpha.0.9", "1.1.0").ShouldBeFalse();
		Older("1.1.1-alpha.0.9", "1.1.1").ShouldBeTrue();
	}

	private static bool Older(string left, string right)
	{
		ReleaseVersion.TryParse(left, out var from);
		ReleaseVersion.TryParse(right, out var to);

		return from.IsOlderThan(to);
	}
}
