using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which running processes and installed packages a person is offered to debug, decided apart from the Win32 that
/// reads them: a process that runs .NET and is not part of Rose, with a window first; and a package's apps but those
/// it keeps out of the Start menu, named as the Start menu names them.
/// </summary>
public sealed class DebugTargetsTests
{
	/// <summary>CoreCLR and the .NET Framework's two runtimes say a process runs .NET, matched ignoring case; anything else does not.</summary>
	[Test]
	public void A_runtime_module_says_a_process_runs_dotnet()
	{
		RunningApps.RunsDotNet(["ntdll.dll", "CoreCLR.dll"]).ShouldBeTrue();
		RunningApps.RunsDotNet(["ntdll.dll", "clr.dll"]).ShouldBeTrue();
		RunningApps.RunsDotNet(["ntdll.dll", "kernel32.dll", "Windows.UI.Xaml.dll"]).ShouldBeFalse();
	}

	/// <summary>Rose's own processes are never offered: attaching to the window showing the list would freeze the list.</summary>
	[Test]
	public void Roses_own_processes_are_not_offered()
	{
		RunningApps.IsRose("RoseMcp.App").ShouldBeTrue();
		RunningApps.IsRose("rosemcp.liveapp").ShouldBeTrue();
		RunningApps.IsRose("Rose").ShouldBeFalse();
		RunningApps.IsRose("DrawboardPDF").ShouldBeFalse();
	}

	/// <summary>A .NET diagnostics pipe names its process, which is how a single-file app with no runtime module is found.</summary>
	[Test]
	[Arguments(@"\\.\pipe\dotnet-diagnostic-1234-56789-socket", 1234)]
	[Arguments("dotnet-diagnostic-42-1-socket", 42)]
	[Arguments("dotnet-diagnostic-x-1-socket", null)]
	[Arguments(@"\\.\pipe\mojo.1234.5678", null)]
	[Arguments("dotnet-diagnostic--1-socket", null)]
	public void A_diagnostics_pipe_names_its_process(string pipe, int? expected) =>
		RunningApps.DiagnosticPipeProcessId(pipe).ShouldBe(expected);

	/// <summary>Those with a window come first, then by what a row calls them: the title, else the name.</summary>
	[Test]
	public void Apps_with_a_window_come_first_then_by_name()
	{
		RunningApp App(int pid, string name, string? title, bool window) =>
			new() { ProcessId = pid, Name = name, WindowTitle = title, HasWindow = window };

		var ordered = RunningApps.Ordered(
		[
			App(1, "zeta", null, window: false),
			App(2, "beta", "Rose Probe", window: true),
			App(3, "alpha", null, window: false),
			App(4, "gamma", "Drawboard PDF", window: true),
		]);

		ordered.Select(app => app.ProcessId).ShouldBe([4, 2, 3, 1]);
		RunningApps.DisplayName(ordered[0]).ShouldBe("Drawboard PDF");
		RunningApps.DisplayName(ordered[2]).ShouldBe("alpha");
	}

	/// <summary>A package's family is its name and its publisher's id; anything not in a full name's shape has none.</summary>
	[Test]
	public void A_full_name_gives_its_family()
	{
		InstalledApps.FamilyOf("Drawboard.PDF_6.90.2.0_x64__gqbn7fs4pywxm").ShouldBe("Drawboard.PDF_gqbn7fs4pywxm");
		InstalledApps.FamilyOf("Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe").ShouldBe("Microsoft.VCLibs.140.00_8wekyb3d8bbwe");
		InstalledApps.FamilyOf("not-a-package").ShouldBeNull();
	}

	/// <summary>
	/// A manifest gives its identity, its display name and its apps, whatever namespaces its schema revision uses; an
	/// app kept out of the Start menu is a package's helper and is not offered.
	/// </summary>
	[Test]
	public void A_manifest_gives_the_apps_a_person_starts()
	{
		const string Manifest = """
			<?xml version="1.0" encoding="utf-8"?>
			<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
			         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
			  <Identity Name="Drawboard.PDF" Publisher="CN=Drawboard" Version="6.90.2.0" />
			  <Properties><DisplayName>ms-resource:AppName</DisplayName></Properties>
			  <Applications>
			    <Application Id="App">
			      <uap:VisualElements DisplayName="Drawboard PDF" />
			    </Application>
			    <Application Id="Helper">
			      <uap:VisualElements DisplayName="Background" AppListEntry="none" />
			    </Application>
			    <Application Id="Second">
			      <uap:VisualElements DisplayName="ms-resource:SecondName" />
			    </Application>
			  </Applications>
			</Package>
			""";

		var manifest = InstalledApps.Read(Manifest);

		manifest.IdentityName.ShouldBe("Drawboard.PDF");
		manifest.DisplayName.ShouldBe("ms-resource:AppName");
		manifest.Apps.ShouldBe(
		[
			new InstalledApps.ManifestApp("App", "Drawboard PDF"),
			new InstalledApps.ManifestApp("Second", "ms-resource:SecondName"),
		]);
	}

	/// <summary>
	/// A resource reference is tried in the package's own resources map first, where a manifest's strings live, then
	/// as written; a full ms-resource URI is taken as it is, and a plain name is not a reference at all.
	/// </summary>
	[Test]
	public void A_resource_reference_is_tried_where_a_manifests_strings_live()
	{
		const string FullName = "Drawboard.PDF_6.90.2.0_x64__gqbn7fs4pywxm";

		InstalledApps.ResourceCandidates("ms-resource:AppName", "Drawboard.PDF", FullName).ShouldBe(
		[
			"@{Drawboard.PDF_6.90.2.0_x64__gqbn7fs4pywxm?ms-resource://Drawboard.PDF/resources/AppName}",
			"@{Drawboard.PDF_6.90.2.0_x64__gqbn7fs4pywxm?ms-resource://Drawboard.PDF/AppName}",
		]);

		InstalledApps.ResourceCandidates("ms-resource:/Strings/AppName", "Drawboard.PDF", FullName).ShouldBe(
		[
			"@{Drawboard.PDF_6.90.2.0_x64__gqbn7fs4pywxm?ms-resource://Drawboard.PDF/Strings/AppName}",
		]);

		InstalledApps.ResourceCandidates("ms-resource://Other/Strings/Name", "Drawboard.PDF", FullName).ShouldBe(
		[
			"@{Drawboard.PDF_6.90.2.0_x64__gqbn7fs4pywxm?ms-resource://Other/Strings/Name}",
		]);

		InstalledApps.ResourceCandidates("Drawboard PDF", "Drawboard.PDF", FullName).ShouldBeEmpty();
		InstalledApps.IsReference("ms-resource:AppName").ShouldBeTrue();
		InstalledApps.IsReference("Drawboard PDF").ShouldBeFalse();
	}

	/// <summary>Packages in development mode -- the ones a build deploys -- come first.</summary>
	[Test]
	public void Development_mode_packages_come_first()
	{
		InstalledApp App(string name, bool development) => new()
		{
			AppUserModelId = $"{name}_x!App",
			DisplayName = name,
			PackageFamilyName = $"{name}_x",
			DevelopmentMode = development,
		};

		InstalledApps.Ordered([App("Calculator", false), App("Rose Probe", true), App("Alarms", false)])
			.Select(app => app.DisplayName)
			.ShouldBe(["Rose Probe", "Alarms", "Calculator"]);
	}
}
