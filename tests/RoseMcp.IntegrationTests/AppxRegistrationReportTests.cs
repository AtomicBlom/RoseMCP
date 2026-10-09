using System.Text.RegularExpressions;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// How a refused registration is read: which framework Windows asked for, what the failure says, and
/// which package in the Windows SDK answers the ask.
/// <para>
/// Tests over the text the registration script writes and over a staged folder of empty files, so
/// they need no probe app, no developer mode and no registration, and run in the plain integration
/// job on every change. The output below is Windows' refusal of a modern UWP probe built for x86 on a
/// machine with no x86 <c>Microsoft.VCLibs.140.00.Debug</c>: the refusal whose headline alone names
/// no framework.
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

	/// <summary>
	/// The SDK's framework packages are told apart by the folder they sit in or a dotted segment of
	/// their name, never by the architecture appearing anywhere in the path: the SDK lives under
	/// <c>Program Files (x86)</c>, so every path there contains "x86", and an x86 ask matched that way
	/// takes whichever package is newest -- here the x64 one.
	/// </summary>
	[Test]
	public void The_sdk_framework_for_an_architecture_is_the_one_in_its_folder_under_program_files_x86()
	{
		var root = Path.Combine(Path.GetTempPath(), $"rose-sdk-{Guid.NewGuid():N}", "Program Files (x86)", "ExtensionSDKs");
		try
		{
			var appx = Path.Combine(root, "Microsoft.VCLibs", "14.0", "AppX");
			var debugX86 = Stage(Path.Combine(appx, "Debug", "x86", "Microsoft.VCLibs.x86.Debug.14.00.appx"), minutesAgo: 30);
			var debugX64 = Stage(Path.Combine(appx, "Debug", "x64", "Microsoft.VCLibs.x64.Debug.14.00.appx"), minutesAgo: 0);
			var retailX86 = Stage(Path.Combine(appx, "Retail", "x86", "Microsoft.VCLibs.x86.14.00.appx"), minutesAgo: 20);
			var retailX64 = Stage(Path.Combine(appx, "Retail", "x64", "Microsoft.VCLibs.x64.14.00.appx"), minutesAgo: 10);

			Find("Microsoft.VCLibs.140.00.Debug", "x86").ShouldBe(debugX86);
			Find("Microsoft.VCLibs.140.00.Debug", "x64").ShouldBe(debugX64);
			Find("Microsoft.VCLibs.140.00", "x86").ShouldBe(retailX86);
			Find("Microsoft.VCLibs.140.00", "X64").ShouldBe(retailX64);
			Find("Microsoft.VCLibs.140.00.Debug", "arm64").ShouldBeNull();
		}
		finally
		{
			Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root))!, recursive: true);
		}

		string? Find(string name, string architecture) =>
			TestToolchain.SdkFramework(new TestToolchain.FrameworkDependency(name, architecture), [root]);

		static string Stage(string path, int minutesAgo)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "not a package");
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
			return path;
		}
	}
}
