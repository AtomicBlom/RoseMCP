namespace RoseMcp.UnitTests;

/// <summary>
/// Whether a project compiles the files in its directory, which is what decides whether a file that
/// has just appeared is in the build or merely near it. Getting this wrong in the generous direction
/// reports diagnostics for a file nothing compiles; getting it wrong in the strict direction hides a
/// file that is compiled, which is the worse of the two and so the way the doubt is resolved.
/// </summary>
public sealed class ProjectItemStyleTests
{
	[Theory]
	[InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup /></Project>""")]
	[InlineData("""<Project><Sdk Name="Microsoft.NET.Sdk" /></Project>""")]
	[InlineData("""<Project><Import Sdk="Microsoft.NET.Sdk" Project="Sdk.props" /></Project>""")]
	public void Reads_an_sdk_project_as_globbing_its_files(string project)
	{
		Assert.True(ProjectItemStyle.GlobsSourceFiles(project));
	}

	/// <summary>
	/// A legacy project lists every file it compiles, so a new file beside its siblings is not in
	/// the build until the project names it -- and this is the case that must not be guessed at,
	/// since UWP and older desktop projects are all of this shape.
	/// </summary>
	[Fact]
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

		Assert.False(ProjectItemStyle.GlobsSourceFiles(project));
	}

	/// <summary>
	/// An SDK project can turn the globs off, and a repository that does it means it: the file list
	/// is then as explicit as a legacy project's.
	/// </summary>
	[Theory]
	[InlineData("EnableDefaultCompileItems")]
	[InlineData("EnableDefaultItems")]
	public void Reads_a_project_that_turns_the_globs_off(string property)
	{
		var project = $"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <{property}>false</{property}>
			  </PropertyGroup>
			</Project>
			""";

		Assert.False(ProjectItemStyle.GlobsSourceFiles(project));
	}

	/// <summary>
	/// Text this cannot read is text it has no opinion about, and no opinion means the SDK default:
	/// a project file that will not parse is one this loaded from a solution that did parse it, so
	/// the failure is here rather than in the project.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("<Project><PropertyGroup></Project>")]
	public void Assumes_the_default_when_it_cannot_tell(string project)
	{
		Assert.True(ProjectItemStyle.GlobsSourceFiles(project));
	}
}
