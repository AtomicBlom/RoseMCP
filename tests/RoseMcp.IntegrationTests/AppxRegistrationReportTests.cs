using System.Text.RegularExpressions;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// How a refused registration is read: which framework Windows asked for, and what the failure says.
/// <para>
/// Pure tests over the text the registration script writes, so they need no probe app, no developer
/// mode and no registration, and run in the plain integration job on every change. The output below
/// is what Windows wrote for a modern UWP probe built for x86 on a machine with no x86
/// <c>Microsoft.VCLibs.140.00.Debug</c>: the refusal whose headline was all a hosted runner reported.
/// </para>
/// </summary>
public sealed class AppxRegistrationReportTests
{
	private const string RefusedForAFramework =
		"""
		ERROR: Deployment failed with HRESULT: 0x80073CF3, Package failed updates, dependency or conflict validation.
		ERROR: Windows cannot install package RoseMcp.ProbeApp.UwpModern_1.0.0.0_x86__m6jgrvk8sw5nm because this package depends on a framework that could not be found. Provide the framework "Microsoft.VCLibs.140.00.Debug" published by "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", with neutral or x86 processor architecture and minimum version 14.0.33519.0, along with this package to install.
		ERROR: NOTE: For additional information, look for [ActivityId] a9b29514-57df-0005-8052-bca9df57dd01 in the Event Log or use the command line Get-AppPackageLog -ActivityID a9b29514-57df-0005-8052-bca9df57dd01
		LOG: Windows cannot install package RoseMcp.ProbeApp.UwpModern_1.0.0.0_x86__m6jgrvk8sw5nm because this package depends on a framework that could not be found. Provide the framework "Microsoft.VCLibs.140.00.Debug" published by "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", with neutral or x86 processor architecture and minimum version 14.0.33519.0, along with this package to install.
		LOG: Deployment Register operation with target volume C: on Package RoseMcp.ProbeApp.UwpModern_1.0.0.0_x86__m6jgrvk8sw5nm from: (AppxManifest.xml) failed with error 0x80073CF3.
		LOG: AppX Deployment operation failed for package RoseMcp.ProbeApp.UwpModern_1.0.0.0_x86__m6jgrvk8sw5nm with error 0x80073CF3. The specific error text for this failure is: Windows cannot install package RoseMcp.ProbeApp.UwpModern_1.0.0.0_x86__m6jgrvk8sw5nm because this package depends on a framework that could not be found. Provide the framework "Microsoft.VCLibs.140.00.Debug" published by "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", with neutral or x86 processor architecture and minimum version 14.0.33519.0, along with this package to install.
		""";

	/// <summary>
	/// The framework is named on a later line of the message, under a headline that names nothing, so a
	/// reader that keeps only the first line never installs anything and every UWP probe stays
	/// unregistered on a machine that lacks one.
	/// </summary>
	[Test]
	public void A_refusal_for_a_missing_framework_names_it_and_its_architecture()
	{
		var read = TestToolchain.ReadRegistration(RefusedForAFramework);

		read.FamilyName.ShouldBeNull();
		read.Reported.ShouldContain("0x80073CF3");
		read.Reported.ShouldContain("Provide the framework \"Microsoft.VCLibs.140.00.Debug\"");
		read.Reported.ShouldContain("[ActivityId] a9b29514-57df-0005-8052-bca9df57dd01");
		read.Reported.ShouldContain("The deployment log for that activity also says: Deployment Register operation");

		// The log's two restatements of the reason are left out, so the reason is said once.
		Regex.Count(read.Reported, "Provide the framework").ShouldBe(1);

		TestToolchain.MissingFramework(read.Reported)
			.ShouldBe(new TestToolchain.FrameworkDependency("Microsoft.VCLibs.140.00.Debug", "x86"));
	}

	/// <summary>A refusal about something else installs nothing, so it is reported as it stands.</summary>
	[Test]
	public void A_refusal_that_is_not_about_a_framework_asks_for_none()
	{
		var read = TestToolchain.ReadRegistration(
			"ERROR: Deployment failed with HRESULT: 0x80073CFF, To install this application you need either a Windows developer license or a sideloading-enabled system.");

		read.FamilyName.ShouldBeNull();
		read.Reported.ShouldContain("developer license");
		TestToolchain.MissingFramework(read.Reported).ShouldBeNull();
	}

	/// <summary>A registration that went through is read as the family name, whatever else was written.</summary>
	[Test]
	public void A_registration_reads_as_its_family_name()
	{
		var read = TestToolchain.ReadRegistration("WARNING: something unrelated\r\nPFN: RoseMcp.ProbeApp.UwpModern_m6jgrvk8sw5nm\r\n");

		read.FamilyName.ShouldBe("RoseMcp.ProbeApp.UwpModern_m6jgrvk8sw5nm");
		read.Reported.ShouldBeEmpty();
	}

	/// <summary>
	/// Silence is not success: a script that printed neither a family name nor an error still failed
	/// to register, and whatever it did print is the only clue to why.
	/// </summary>
	[Test]
	public void Output_with_neither_a_family_name_nor_an_error_is_a_failure_that_quotes_it()
	{
		TestToolchain.ReadRegistration(string.Empty).Reported.ShouldBe("Add-AppxPackage reported nothing and the package is not registered.");

		var read = TestToolchain.ReadRegistration("powershell : the term is not recognised");
		read.FamilyName.ShouldBeNull();
		read.Reported.ShouldContain("powershell : the term is not recognised");
	}
}
