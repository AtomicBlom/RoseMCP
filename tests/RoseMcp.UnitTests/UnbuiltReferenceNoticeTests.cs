using RoseMcp.Solutions;

using static RoseMcp.Worker.WorkspaceStatusReporter;

namespace RoseMcp.UnitTests;

/// <summary>
/// "Cannot resolve Assembly or Windows Metadata file" said with its remedy. MSBuild names a path and
/// nothing else; when that path is a project in the same solution, the fix is to build that project and
/// reload, and the notice says which project and how.
/// </summary>
public sealed class UnbuiltReferenceNoticeTests
{
	private static readonly ProjectOutput Contracts =
		new("Contracts", @"D:\repo\Contracts\Contracts.csproj", @"D:\repo\Contracts\bin\Debug\net10.0\Contracts.dll");

	private static readonly ProjectOutput Inspector =
		new("Inspector", @"D:\repo\Inspector\Inspector.csproj", @"D:\repo\Inspector\bin\x64\Debug\Inspector.dll");

	private static readonly ProjectOutput[] Projects = [Contracts, Inspector];

	/// <summary>The case as status reports it on a fresh worktree: the project to build, what wanted it, and the command.</summary>
	[Test]
	public void Names_the_project_to_build_what_wanted_it_and_how()
	{
		var notice = UnbuiltReferenceNotice(null, [Unresolved("Inspector", Contracts.OutputFilePath)], Projects, _ => false);

		notice.ShouldNotBeNull();
		notice.ShouldContain("Contracts (wanted by Inspector)", Case.Sensitive);
		notice.ShouldContain(@"dotnet build ""D:\repo\Contracts\Contracts.csproj""", Case.Sensitive);
		notice.ShouldContain("then rose_workspace_reload", Case.Sensitive);
	}

	/// <summary>One project wanted by several, over several diagnostics, is one entry naming each that wanted it.</summary>
	[Test]
	public void Folds_every_project_that_wanted_one_output_into_one_entry()
	{
		var notice = UnbuiltReferenceNotice(
			null,
			[
				Unresolved("Tray", Contracts.OutputFilePath),
				Unresolved("Inspector", Contracts.OutputFilePath),
				Unresolved("Inspector", Contracts.OutputFilePath),
			],
			Projects,
			_ => false);

		notice.ShouldNotBeNull();
		notice.ShouldContain("Contracts (wanted by Inspector, Tray)", Case.Sensitive);
		notice.ShouldContain("because it has not been built", Case.Sensitive);
	}

	/// <summary>A design-time build under other properties looks in another folder for the same file, which is still that project's.</summary>
	[Test]
	public void Matches_an_output_looked_for_in_another_folder_by_its_file_name()
	{
		var notice = UnbuiltReferenceNotice(
			null,
			[Unresolved("Inspector", @"D:\repo\Contracts\bin\x64\Debug\net10.0\Contracts.dll")],
			Projects,
			_ => false);

		notice.ShouldNotBeNull().ShouldContain(@"dotnet build ""D:\repo\Contracts\Contracts.csproj""", Case.Sensitive);
	}

	/// <summary>Built since the load, so the only step left is the reload; telling the caller to build again would be noise.</summary>
	[Test]
	public void Says_only_reload_once_the_output_exists()
	{
		var notice = UnbuiltReferenceNotice(null, [Unresolved("Inspector", Contracts.OutputFilePath)], Projects, _ => true);

		notice.ShouldNotBeNull();
		notice.ShouldContain("has been built since, so rose_workspace_reload clears this", Case.Sensitive);
		notice.ShouldNotContain("dotnet build", Case.Sensitive);
	}

	/// <summary>A file no project here writes is a package or SDK reference, and naming a project to build for it would be wrong advice.</summary>
	[Test]
	public void Says_nothing_about_a_file_no_project_writes()
	{
		UnbuiltReferenceNotice(null, [Unresolved("Inspector", @"C:\nuget\Some.Package.dll")], Projects, _ => false).ShouldBeNull();
		UnbuiltReferenceNotice(null, ["Found project reference without a matching metadata reference: A.csproj"], Projects, _ => false)
			.ShouldBeNull();
	}

	/// <summary>
	/// A platform this server chose and that looks wrong has its own reason, which says nothing was built for it;
	/// advice to build the projects as well would send the caller to build for the platform that is the mistake.
	/// </summary>
	[Test]
	public void Defers_to_a_suspected_platform()
	{
		var build = new BuildProperties
		{
			Platform = "ARM64",
			PlatformWasChosen = true,
			Available = new SolutionConfigurations { Platforms = ["x64", "ARM64"] },
		};

		UnbuiltReferenceNotice(
			build,
			[Unresolved("Inspector", @"D:\repo\Contracts\bin\ARM64\Debug\net10.0\Contracts.dll")],
			Projects,
			_ => false).ShouldBeNull();
	}

	/// <summary>The diagnostic as MSBuild words it for a project whose design-time build could not find a reference.</summary>
	private static string Unresolved(string wanting, string path) =>
		$@"Msbuild failed when processing the file 'D:\repo\{wanting}\{wanting}.csproj' with message: "
		+ $"Cannot resolve Assembly or Windows Metadata file '{path}'";
}
