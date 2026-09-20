using static RoseMcp.Worker.RestoreRunner;

namespace RoseMcp.UnitTests;

/// <summary>
/// The postcondition a restore is judged by: whether a project came out of it holding output a load
/// can use.
/// <para>
/// Worth its own fixture because that answer alone decides whether a workspace calls itself
/// degraded, and the whole check is a filesystem probe -- there is nothing to reason about without a
/// directory staged for it to read.
/// </para>
/// </summary>
public sealed class RestoreOutputTests
{
	/// <summary>
	/// The reading these exist to rule out. NuGet decides freshness by the hash in
	/// <c>project.nuget.cache</c>, not by modification time, so editing <c>global.json</c> -- an SDK
	/// pin with nothing to say about packages -- leaves every assets file older than an input while
	/// restore, correctly, rewrites none of them. Judging the postcondition on timestamps reports a
	/// fully restored solution as holding no restore output at all, and the reason printed blames
	/// non-SDK projects in a solution that has none.
	/// </summary>
	[Test]
	public void An_assets_file_older_than_its_inputs_is_still_restore_output()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-restore-");
		try
		{
			var project = StageProject(root, "App", assets: "{}", cache: Cache(success: true));

			var ambient = Path.Combine(root.FullName, "global.json");
			File.WriteAllText(ambient, "{ \"sdk\": { \"version\": \"10.0.401\" } }");

			// The order a no-op restore leaves behind: the ambient file edited after the assets file was
			// last written, and restore declining to write it again because no package input moved.
			File.SetLastWriteTimeUtc(project.Assets, DateTime.UtcNow.AddHours(-1));
			File.SetLastWriteTimeUtc(ambient, DateTime.UtcNow);

			Assert.False(HasNoRestoreOutput(project.File));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// The case the check is for: <c>dotnet restore</c> passes silently over a project it does not
	/// understand and exits 0 having written it nothing.
	/// </summary>
	[Test]
	public void A_project_with_no_assets_file_has_no_restore_output()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-restore-");
		try
		{
			var project = StageProject(root, "Legacy", assets: null);

			Assert.True(HasNoRestoreOutput(project.File));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	[Test]
	public void A_project_with_no_obj_directory_at_all_has_no_restore_output()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-restore-");
		try
		{
			var project = Path.Combine(root.FullName, "Fresh", "Fresh.csproj");
			Directory.CreateDirectory(Path.GetDirectoryName(project)!);
			File.WriteAllText(project, "<Project />");

			Assert.True(HasNoRestoreOutput(project));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// A solution restore exits 0 when one project inside it fails, so the exit code cannot be what
	/// reports that project. Its own cache is where it says so.
	/// </summary>
	[Test]
	public void An_assets_file_whose_cache_records_a_failure_is_not_restore_output()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-restore-");
		try
		{
			var project = StageProject(root, "Broken", assets: "{}", cache: Cache(success: false));

			Assert.True(HasNoRestoreOutput(project.File));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// A cache that cannot be read decides nothing. The assets file beside it is already evidence
	/// restore produced something, and one unparseable file must not be what declares a whole
	/// workspace degraded.
	/// </summary>
	[Test]
	[Arguments("")]
	[Arguments("not json at all")]
	[Arguments("[]")]
	[Arguments("{ \"version\": 2 }")]
	public void An_unreadable_cache_leaves_the_assets_file_speaking_for_itself(string cache)
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-restore-");
		try
		{
			var project = StageProject(root, "Odd", assets: "{}", cache: cache);

			Assert.False(HasNoRestoreOutput(project.File));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// A repository that keeps one restore per SDK version moves BaseIntermediateOutputPath, so the
	/// assets file sits a level below <c>obj/</c> and the cache sits beside it there rather than at
	/// the top.
	/// </summary>
	[Test]
	public void An_assets_file_below_obj_is_found_with_the_cache_beside_it()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-restore-");
		try
		{
			var project = StageProject(root, "Addin", assets: "{}", cache: Cache(success: true), below: "2027");

			Assert.False(HasNoRestoreOutput(project.File));

			File.WriteAllText(project.Cache!, Cache(success: false));

			Assert.True(HasNoRestoreOutput(project.File));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// One restore of several failing says nothing about the project: only the set holding no usable
	/// output at all does. A project restored under four sets of properties keeps one assets file per
	/// set, and a load reads the one that matches the properties it was given.
	/// </summary>
	[Test]
	public void One_good_assets_file_among_several_is_enough()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-restore-");
		try
		{
			var project = StageProject(root, "Addin", assets: "{}", cache: Cache(success: false), below: "2026");
			StageAssets(Path.Combine(root.FullName, "Addin", "obj", "2027"), assets: "{}", cache: Cache(success: true));

			Assert.False(HasNoRestoreOutput(project.File));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	private static string Cache(bool success) =>
		$"{{ \"version\": 2, \"dgSpecHash\": \"vVBYfzCoCrg=\", \"success\": {(success ? "true" : "false")} }}";

	/// <summary>
	/// A project directory with the restore output a test wants it to have. <paramref name="below"/>
	/// names a subdirectory of <c>obj/</c> for the moved-BaseIntermediateOutputPath layout; the
	/// default is the ordinary one.
	/// </summary>
	private static (string File, string Assets, string? Cache) StageProject(
		DirectoryInfo root,
		string name,
		string? assets,
		string? cache = null,
		string below = "")
	{
		var directory = Path.Combine(root.FullName, name);
		Directory.CreateDirectory(directory);

		var file = Path.Combine(directory, $"{name}.csproj");
		File.WriteAllText(file, "<Project />");

		var obj = Path.Combine(directory, "obj");

		// The directory exists either way. A build makes one whether or not restore ever wrote into it,
		// so its absence is not what distinguishes a project restore passed over.
		Directory.CreateDirectory(obj);

		if (assets is null) return (file, Path.Combine(obj, "project.assets.json"), null);

		var staged = StageAssets(Path.Combine(obj, below), assets, cache);
		return (file, staged.Assets, staged.Cache);
	}

	private static (string Assets, string? Cache) StageAssets(string directory, string assets, string? cache)
	{
		Directory.CreateDirectory(directory);

		var assetsPath = Path.Combine(directory, "project.assets.json");
		File.WriteAllText(assetsPath, assets);

		if (cache is null) return (assetsPath, null);

		var cachePath = Path.Combine(directory, "project.nuget.cache");
		File.WriteAllText(cachePath, cache);

		return (assetsPath, cachePath);
	}
}
