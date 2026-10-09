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
	/// <summary>
	/// A repository at the filesystem root, absolute on every OS and joined with its separator, because the
	/// notice names projects by <see cref="Path.GetFileNameWithoutExtension(string)"/> and matches outputs by
	/// <see cref="Path.GetFileName(string)"/>, and off Windows a backslash is part of a file name rather than a
	/// separator. The root and not the temporary directory under it: these paths ride inside MSBuild's quoted
	/// message, which an apostrophe in a user's temporary path would end early. Nothing here touches disk.
	/// </summary>
	private static readonly string Repo = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "repo");

	private static readonly ProjectOutput Contracts =
		new("Contracts", InRepo("Contracts", "Contracts.csproj"), InRepo("Contracts", "bin", "Debug", "net10.0", "Contracts.dll"));

	private static readonly ProjectOutput Inspector =
		new("Inspector", InRepo("Inspector", "Inspector.csproj"), InRepo("Inspector", "bin", "x64", "Debug", "Inspector.dll"));

	private static readonly ProjectOutput[] Projects = [Contracts, Inspector];

	/// <summary>The case as status reports it on a fresh worktree: the project to build, what wanted it, and the command.</summary>
	[Test]
	public void Names_the_project_to_build_what_wanted_it_and_how()
	{
		var notice = UnbuiltReferenceNotice(null, [Unresolved("Inspector", Contracts.OutputFilePath)], Projects, _ => false);

		notice.ShouldNotBeNull();
		notice.ShouldContain("Contracts (wanted by Inspector)", Case.Sensitive);
		notice.ShouldContain($"dotnet build \"{Contracts.FilePath}\"", Case.Sensitive);
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

	/// <summary>
	/// A design-time build under other properties looks in another folder for the same file, which is still that
	/// project's -- and the build named has to be under those properties, or it writes to the folder the load does
	/// not look in and the notice never clears.
	/// </summary>
	[Test]
	public void Names_the_build_under_the_properties_the_load_used()
	{
		var build = new BuildProperties
		{
			Configuration = "Debug-2027",
			Platform = "x64",
			Extra = new Dictionary<string, string> { ["RevitVersion"] = "2027" },
		};

		var notice = UnbuiltReferenceNotice(
			build,
			[Unresolved("Inspector", InRepo("Contracts", "bin", "x64", "Debug-2027", "net10.0", "Contracts.dll"))],
			Projects,
			_ => false);

		notice.ShouldNotBeNull().ShouldContain($"dotnet build \"{Contracts.FilePath}\" ", Case.Sensitive);
		notice.ShouldContain("-p:Platform=x64", Case.Sensitive);
		notice.ShouldContain("-p:Configuration=Debug-2027", Case.Sensitive);
		notice.ShouldContain("-p:RevitVersion=2027", Case.Sensitive);
	}

	/// <summary>A project that names no SDK, which is every UWP one, cannot be built by dotnet build, so the command is MSBuild's.</summary>
	[Test]
	public void Names_MSBuild_for_a_project_that_names_no_SDK()
	{
		var legacy = Contracts with { NamesSdk = false };

		var notice = UnbuiltReferenceNotice(null, [Unresolved("Inspector", Contracts.OutputFilePath)], [legacy, Inspector], _ => false);

		notice.ShouldNotBeNull().ShouldContain($"msbuild \"{Contracts.FilePath}\"", Case.Sensitive);
		notice.ShouldNotContain("dotnet build \"", Case.Sensitive);
		notice.ShouldContain("is built with MSBuild", Case.Sensitive);
	}

	/// <summary>
	/// The fresh clone of a solution declaring several platforms: the platform chosen to match the host, and
	/// nothing built under any of them. Switching platform cures nothing, so the remedy is the build, under the
	/// platform that was chosen.
	/// </summary>
	[Test]
	public void Names_the_build_on_a_fresh_clone_rather_than_another_platform()
	{
		var build = new BuildProperties
		{
			Platform = "x64",
			PlatformWasChosen = true,
			Available = new SolutionConfigurations { Platforms = ["x64", "x86", "ARM64"] },
		};
		string[] messages = [Unresolved("Inspector", InRepo("Contracts", "bin", "x64", "Debug", "net10.0", "Contracts.dll"))];

		build.SuspectWrongPlatform(messages, _ => false).ShouldBeNull();

		var notice = UnbuiltReferenceNotice(build, messages, Projects, _ => false);
		notice.ShouldNotBeNull().ShouldContain("-p:Platform=x64", Case.Sensitive);
		notice.ShouldNotContain("platform=x86", Case.Sensitive);
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
		UnbuiltReferenceNotice(null, [Unresolved("Inspector", InRepo("packages", "Some.Package.dll"))], Projects, _ => false).ShouldBeNull();
		UnbuiltReferenceNotice(null, ["Found project reference without a matching metadata reference: A.csproj"], Projects, _ => false)
			.ShouldBeNull();
	}

	/// <summary>
	/// A platform this server chose, with the outputs built under another declared one, has its own reason saying
	/// to reload under that one; advice to build as well would send the caller to build for the platform that is
	/// the mistake.
	/// </summary>
	[Test]
	public void Defers_to_a_platform_the_outputs_were_built_for()
	{
		var build = new BuildProperties
		{
			Platform = "x64",
			PlatformWasChosen = true,
			Available = new SolutionConfigurations { Platforms = ["x64", "x86", "ARM64"] },
		};
		string[] messages = [Unresolved("Inspector", InRepo("Contracts", "bin", "x64", "Debug", "net10.0", "Contracts.dll"))];
		var x86Folder = $"{Path.DirectorySeparatorChar}x86{Path.DirectorySeparatorChar}";
		Func<string, bool> builtForX86 = path => path.Contains(x86Folder, StringComparison.Ordinal);

		build.SuspectWrongPlatform(messages, builtForX86).ShouldNotBeNull().ShouldContain("platform=x86", Case.Sensitive);
		UnbuiltReferenceNotice(build, messages, Projects, builtForX86).ShouldBeNull();
	}

	/// <summary>The diagnostic as MSBuild words it for a project whose design-time build could not find a reference.</summary>
	private static string Unresolved(string wanting, string path) =>
		$"Msbuild failed when processing the file '{InRepo(wanting, $"{wanting}.csproj")}' with message: "
		+ $"Cannot resolve Assembly or Windows Metadata file '{path}'";

	/// <summary>A path under <see cref="Repo"/>, joined with this OS's separator.</summary>
	private static string InRepo(params string[] segments) => Path.Combine([Repo, .. segments]);
}
