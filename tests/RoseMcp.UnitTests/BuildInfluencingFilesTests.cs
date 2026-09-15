using RoseMcp.Solutions;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which paths a file watcher remembers, which is the whole of what it hands a read. Everything else a
/// read finds for itself, however much of it changed.
/// <para>
/// The paths are written with forward slashes because this suite also runs on Linux, where a backslash
/// is part of a file name: <c>Path.GetFileName</c> of a Windows-style path there is the whole path, and
/// a rule matched by name would be tested against nothing. Every platform takes a forward slash as a
/// separator.
/// </para>
/// </summary>
public sealed class BuildInfluencingFilesTests
{
	[Test]
	[Arguments("/repo/src/App/App.csproj")]
	[Arguments("/repo/Repo.slnx")]
	[Arguments("/repo/Repo.sln")]
	[Arguments("/repo/build/Common.props")]
	[Arguments("/repo/build/Common.TARGETS")]
	[Arguments("/repo/src/Directory.Build.props")]
	[Arguments("/repo/global.json")]
	[Arguments("/repo/src/.editorconfig")]
	[Arguments("/repo/src/App/packages.config")]
	public void A_project_solution_import_or_named_build_file_is_a_build_file(string path)
	{
		Assert.True(BuildInfluencingFiles.IsBuildFile(path), $"{path} decides how a project evaluates");
	}

	/// <summary>
	/// Source and data files are not, however many of them change. A read stats and walks for those
	/// itself, and remembering them is what would make the number of events a reason to reload.
	/// </summary>
	[Test]
	[Arguments("/repo/src/App/Widget.cs")]
	[Arguments("/repo/src/App/appsettings.json")]
	[Arguments("/repo/src/App/Page.xaml")]
	[Arguments("/repo/src/App/README.md")]
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
		Assert.True(BuildInfluencingFiles.IsImportable("/repo/build/Common.props"));
		Assert.True(BuildInfluencingFiles.IsImportable("/repo/Directory.Build.TARGETS"));
		Assert.False(BuildInfluencingFiles.IsImportable("/repo/src/App/App.csproj"));
		Assert.False(BuildInfluencingFiles.IsImportable("/repo/global.json"));
	}
}
