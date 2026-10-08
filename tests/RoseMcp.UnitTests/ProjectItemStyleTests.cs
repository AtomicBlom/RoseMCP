
namespace RoseMcp.UnitTests;

/// <summary>
/// Whether a project compiles the files in its directory, which is what decides whether a file that
/// has just appeared is in the build or merely near it. Getting this wrong in the generous direction
/// reports diagnostics for a file nothing compiles; getting it wrong in the strict direction hides a
/// file that is compiled, which is the worse of the two and so the way the doubt is resolved.
/// </summary>
public sealed class ProjectItemStyleTests
{
	[Test]
	[Arguments("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup /></Project>""")]
	[Arguments("""<Project><Sdk Name="Microsoft.NET.Sdk" /></Project>""")]
	[Arguments("""<Project><Import Sdk="Microsoft.NET.Sdk" Project="Sdk.props" /></Project>""")]
	public void Reads_an_sdk_project_as_globbing_its_files(string project)
	{
		ProjectItemStyle.GlobsSourceFiles(project).ShouldBeTrue();
	}

	/// <summary>
	/// A legacy project lists every file it compiles, so a new file beside its siblings is not in
	/// the build until the project names it -- and this is the case that must not be guessed at,
	/// since UWP and older desktop projects are all of this shape.
	/// </summary>
	[Test]
	public void Reads_a_legacy_project_as_listing_its_files()
	{
		var project = """
			<?xml version="1.0" encoding="utf-8"?>
			<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
			  <Import Project="$(MSBuildExtensionsPath)\Microsoft.Common.props" />
			  <ItemGroup>
			    <Compile Include="MainPage.xaml.cs" />
			  </ItemGroup>
			</Project>
			""";

		ProjectItemStyle.GlobsSourceFiles(project).ShouldBeFalse("a legacy project lists its files rather than globbing them");
	}

	/// <summary>
	/// Whether a project that lists its files names this one: a literal Compile item, relative to the
	/// project, either separator, among several in one attribute. A wildcard or a property is left to
	/// evaluation and does not count, and a Remove takes the name back.
	/// </summary>
	[Test]
	[Arguments("""<Compile Include="Views\Page.cs" />""", true)]
	[Arguments("""<Compile Include="Views/Page.cs" />""", true)]
	[Arguments("""<Compile Include="Other.cs;Views\Page.cs" />""", true)]
	[Arguments("""<Compile Include="Views\*.cs" />""", false)]
	[Arguments("""<Compile Include="$(Shared)\Views\Page.cs" />""", false)]
	[Arguments("""<Compile Include="Views\Page.cs" /><Compile Remove="Views\Page.cs" />""", false)]
	[Arguments("""<Compile Include="Views\Other.cs" />""", false)]
	public void Says_whether_a_listing_project_names_a_file(string items, bool expected)
	{
		var directory = Path.Combine(Path.GetTempPath(), "listing");
		var project = $"""
			<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
			  <ItemGroup>{items}</ItemGroup>
			</Project>
			""";

		ProjectItemStyle.Lists(project, directory, Path.Combine(directory, "Views", "Page.cs")).ShouldBe(expected);
	}

	/// <summary>
	/// An SDK project can turn the globs off, and a repository that does it means it: the file list
	/// is then as explicit as a legacy project's.
	/// </summary>
	[Test]
	[Arguments("EnableDefaultCompileItems")]
	[Arguments("EnableDefaultItems")]
	public void Reads_a_project_that_turns_the_globs_off(string property)
	{
		var project = $"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <{property}>false</{property}>
			  </PropertyGroup>
			</Project>
			""";

		ProjectItemStyle.GlobsSourceFiles(project).ShouldBeFalse("a project that turns the globs off does not glob");
	}

	/// <summary>
	/// Text this cannot read is text it has no opinion about, and no opinion means the SDK default:
	/// a project file that will not parse is one this loaded from a solution that did parse it, so
	/// the failure is here rather than in the project.
	/// </summary>
	[Test]
	[Arguments("")]
	[Arguments("   ")]
	[Arguments("<Project><PropertyGroup></Project>")]
	public void Assumes_the_default_when_it_cannot_tell(string project)
	{
		ProjectItemStyle.GlobsSourceFiles(project).ShouldBeTrue();
	}
}
