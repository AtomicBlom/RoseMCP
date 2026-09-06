using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Reading a packaged app's architecture out of its package identity (#117).
/// <para>
/// The parse is what is testable here. <c>ForPackage</c> itself asks Windows which packages are
/// registered in a family, so it needs a registered package to say anything -- that half is covered
/// by the live-app suite, which registers real probe apps and debugs them.
/// </para>
/// <para>
/// The names below are real ones, read off a machine rather than invented, because the whole value
/// of this over assuming x64 is that it matches what Windows actually registered.
/// </para>
/// </summary>
public sealed class TargetArchitectureProbeTests
{
	[Theory]
	[InlineData("Microsoft.Paint_11.2605.81.0_x64__8wekyb3d8bbwe", TargetArchitecture.X64)]
	[InlineData("Microsoft.WindowsCalculator_11.2607.0.0_x64__8wekyb3d8bbwe", TargetArchitecture.X64)]
	[InlineData("RoseMcp.ProbeApp.UwpModern_1.0.0.0_x86__m6jgrvk8sw5nm", TargetArchitecture.X86)]
	[InlineData("RoseMcp.ProbeApp.UwpModern_1.0.0.0_arm64__m6jgrvk8sw5nm", TargetArchitecture.Arm64)]
	public void Reads_the_architecture_a_full_name_carries(string fullName, TargetArchitecture expected)
	{
		Assert.Equal(expected, TargetArchitectureProbe.ArchitectureFromFullName(fullName));
	}

	/// <summary>
	/// Neither of these names an architecture a host exists for, and both must come back Unknown so
	/// the launcher falls back to the broker's own rather than picking one. A neutral package is
	/// managed code with no architecture of its own; <c>arm</c> is 32-bit ARM, which nothing here
	/// builds a host for.
	/// </summary>
	[Theory]
	[InlineData("Contoso.App_1.0.0.0_neutral__8wekyb3d8bbwe")]
	[InlineData("Contoso.App_1.0.0.0_arm__8wekyb3d8bbwe")]
	public void An_architecture_with_no_host_is_unknown(string fullName)
	{
		Assert.Equal(TargetArchitecture.Unknown, TargetArchitectureProbe.ArchitectureFromFullName(fullName));
	}

	/// <summary>
	/// Anything that is not a package full name is Unknown rather than an exception or a guess. A
	/// family name is the likeliest thing to arrive here by mistake, since it is the half of an AUMID
	/// this is given, and it has three fields rather than five.
	/// </summary>
	[Theory]
	[InlineData("RoseMcp.ProbeApp.UwpModern_m6jgrvk8sw5nm")]
	[InlineData("Microsoft.Paint")]
	[InlineData("")]
	[InlineData("a_b_c_d_e_f")]
	public void A_name_that_is_not_a_full_name_is_unknown(string name)
	{
		Assert.Equal(TargetArchitecture.Unknown, TargetArchitectureProbe.ArchitectureFromFullName(name));
	}

	/// <summary>
	/// Windows spells these lower-case, but the value decides which host directory is looked in, so
	/// matching cannot depend on a casing nobody here controls.
	/// </summary>
	[Fact]
	public void Matching_ignores_case()
	{
		Assert.Equal(
			TargetArchitecture.Arm64,
			TargetArchitectureProbe.ArchitectureFromFullName("Contoso.App_1.0.0.0_ARM64__8wekyb3d8bbwe"));
	}

	/// <summary>
	/// An AUMID is not a full name either, and this is the one that would be a real bug, because it
	/// is what callers actually hold. Splitting it on '!' is <c>ForPackage</c>'s job, and what is
	/// left is a family name, which carries no architecture at all.
	/// </summary>
	[Fact]
	public void An_aumid_carries_no_architecture()
	{
		Assert.Equal(
			TargetArchitecture.Unknown,
			TargetArchitectureProbe.ArchitectureFromFullName("RoseMcp.ProbeApp.UwpModern_m6jgrvk8sw5nm!App"));
	}
}
