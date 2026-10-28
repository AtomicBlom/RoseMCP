using RoseMcp.Worker.Xaml;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which markup a project is taken to own, read from its project file and its folder the way the SDK's default glob
/// would. A file it does not own is attached to it and fed to its generators, and markup nothing in the project compiles
/// raises a workspace reason that is not true of the project.
/// </summary>
public sealed class XamlItemReaderTests
{
	/// <summary>
	/// An agent's worktrees sit under <c>.claude</c> inside the repository, each a whole copy of it. The SDK never globs a
	/// folder whose name starts with a dot, so none of that markup is the project's.
	/// </summary>
	[Test]
	public void Skips_every_folder_whose_name_starts_with_a_dot()
	{
		using var project = new StagedProject("<Project Sdk=\"Microsoft.NET.Sdk\" />");
		project.Write("Views/MainView.xaml");
		project.Write(".claude/worktrees/other/Views/MainView.xaml");
		project.Write("Views/.hidden/Stray.xaml");

		project.Read().ShouldBe(["Views/MainView.xaml"]);
	}

	/// <summary>A project that holds a submodule of other projects keeps their files out with <c>DefaultItemExcludes</c>.</summary>
	[Test]
	public void Honours_the_folders_the_project_excludes_from_its_default_items()
	{
		using var project = new StagedProject(
			"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <DefaultItemExcludes>$(DefaultItemExcludes);Engine/**;tools\**\*</DefaultItemExcludes>
			  </PropertyGroup>
			</Project>
			""");
		project.Write("MainView.xaml");
		project.Write("Engine/Engine.Gallery/UI/PanelView.xaml");
		project.Write("tools/Editor/EditorView.xaml");
		project.Write("bin/Debug/Copied.xaml");
		project.Write("Engineering/Kept.xaml");

		project.Read().ShouldBe(["Engineering/Kept.xaml", "MainView.xaml"]);
	}

	/// <summary>Whether a conditioned exclude applies depends on properties nothing here evaluates, so the file is kept.</summary>
	[Test]
	public void Does_not_honour_an_exclude_a_condition_decides()
	{
		using var project = new StagedProject(
			"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
			    <DefaultItemExcludes>$(DefaultItemExcludes);Debugging/**</DefaultItemExcludes>
			  </PropertyGroup>
			</Project>
			""");
		project.Write("Debugging/DebugView.xaml");

		project.Read().ShouldBe(["Debugging/DebugView.xaml"]);
	}

	/// <summary>An exclude keeps a folder out of the default glob only; an item the project names outright is still its own.</summary>
	[Test]
	public void Keeps_an_item_the_project_names_inside_an_excluded_folder()
	{
		using var project = new StagedProject(
			"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <DefaultItemExcludes>$(DefaultItemExcludes);Shared/**</DefaultItemExcludes>
			  </PropertyGroup>
			  <ItemGroup>
			    <Page Include="Shared/Theme.xaml" />
			  </ItemGroup>
			</Project>
			""");
		project.Write("Shared/Theme.xaml");
		project.Write("Shared/Unused.xaml");

		project.Read().ShouldBe(["Shared/Theme.xaml"]);
	}

	/// <summary>A project file in a temp folder, with markup written beneath it and read back relative to it.</summary>
	private sealed class StagedProject : IDisposable
	{
		private readonly string _root = Directory.CreateTempSubdirectory("rosemcp-xaml-items-").FullName;

		public StagedProject(string projectFile) => File.WriteAllText(ProjectPath, projectFile);

		private string ProjectPath => Path.Combine(_root, "Game.csproj");

		public void Write(string relativePath)
		{
			var path = Path.Combine(_root, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "<UserControl />");
		}

		public string[] Read() =>
		[
			.. XamlItemReader.Read(ProjectPath)
				.Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
				.Order(StringComparer.Ordinal),
		];

		public void Dispose() => Directory.Delete(_root, recursive: true);
	}
}
