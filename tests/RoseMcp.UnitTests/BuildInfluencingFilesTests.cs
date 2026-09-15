using RoseMcp.Solutions;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which paths a file watcher remembers, which is the whole of what it hands a read. Everything else a
/// read finds for itself, however much of it changed.
/// </summary>
public sealed class BuildInfluencingFilesTests
{
	[Test]
	[Arguments(@"C:\repo\src\App\App.csproj")]
	[Arguments(@"C:\repo\Repo.slnx")]
	[Arguments(@"C:\repo\Repo.sln")]
	[Arguments(@"C:\repo\build\Common.props")]
	[Arguments(@"C:\repo\build\Common.TARGETS")]
	[Arguments(@"C:\repo\src\Directory.Build.props")]
	[Arguments(@"C:\repo\global.json")]
	[Arguments(@"C:\repo\src\.editorconfig")]
	[Arguments(@"C:\repo\src\App\packages.config")]
	public void A_project_solution_import_or_named_build_file_is_a_build_file(string path)
	{
		Assert.True(BuildInfluencingFiles.IsBuildFile(path), $"{path} decides how a project evaluates");
	}

	/// <summary>
	/// Source and data files are not, however many of them change. A read stats and walks for those
	/// itself, and remembering them is what would make the number of events a reason to reload.
	/// </summary>
	[Test]
	[Arguments(@"C:\repo\src\App\Widget.cs")]
	[Arguments(@"C:\repo\src\App\appsettings.json")]
	[Arguments(@"C:\repo\src\App\Page.xaml")]
	[Arguments(@"C:\repo\src\App\README.md")]
	public void A_source_or_data_file_is_not_a_build_file(string path)
	{
		Assert.False(BuildInfluencingFiles.IsBuildFile(path), $"{path} is found by a read without the watcher");
	}

	/// <summary>
	/// Only a <c>.props</c> or a <c>.targets</c> can be imported, which is the question asked for a
	/// project that could not be evaluated and so has no import list to consult.
	/// </summary>
	[Test]
	public void Only_props_and_targets_are_importable()
	{
		Assert.True(BuildInfluencingFiles.IsImportable(@"C:\repo\build\Common.props"));
		Assert.True(BuildInfluencingFiles.IsImportable(@"C:\repo\Directory.Build.TARGETS"));
		Assert.False(BuildInfluencingFiles.IsImportable(@"C:\repo\src\App\App.csproj"));
		Assert.False(BuildInfluencingFiles.IsImportable(@"C:\repo\global.json"));
	}
}
