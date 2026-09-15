using Microsoft.CodeAnalysis;

namespace RoseMcp.UnitTests;

/// <summary>
/// When a build file the sweep does not track is still a reason to reload: only when some project could
/// not be evaluated, and so has no import list for the sweep to watch.
/// </summary>
public sealed class DiskSynchronizerTests
{
	/// <summary>
	/// A project that could not be evaluated has no known imports, so a <c>.props</c> or <c>.targets</c>
	/// changing anywhere might be one of them -- and reloading is the only answer that cannot be stale.
	/// </summary>
	[Test]
	public void An_untracked_import_changing_reloads_when_a_project_could_not_be_evaluated()
	{
		using var tree = LoadedTree.Create();
		var synchronizer = new DiskSynchronizer();

		synchronizer.Reset(
			tree.Solution,
			tree.SolutionPath,
			new EvaluationInputs(new Dictionary<string, IReadOnlySet<string>>(), [tree.ProjectPath]));

		Assert.True(synchronizer.UntrackedImportChanged([tree.PathTo("build", "Shared.props")]));
		Assert.False(
			synchronizer.UntrackedImportChanged([tree.PathTo("App", "App.csproj.user")]),
			"only a file a project could import counts");
	}

	/// <summary>
	/// With every project evaluated, each import is tracked and statted by the sweep, so a build file
	/// outside that set cannot change how anything builds.
	/// </summary>
	[Test]
	public void An_untracked_import_changing_is_no_reason_when_every_project_was_evaluated()
	{
		using var tree = LoadedTree.Create();
		var synchronizer = new DiskSynchronizer();

		synchronizer.Reset(
			tree.Solution,
			tree.SolutionPath,
			new EvaluationInputs(
				new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
				{
					[tree.ProjectPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				},
				[]));

		Assert.False(synchronizer.UntrackedImportChanged([tree.PathTo("build", "Shared.props")]));
	}

	/// <summary>
	/// A build file appearing where none was at load is found by looking again, not by the watcher having
	/// heard it -- so a watcher that lost the event, or never started, still sends the session round a
	/// reload.
	/// </summary>
	[Test]
	[Arguments("App", "Directory.Build.props")]
	[Arguments("", "Directory.Packages.props")]
	[Arguments("", ".editorconfig")]
	[Arguments("App", "packages.config")]
	[Arguments("", "rosemcp.json")]
	public async Task A_build_file_appearing_where_none_was_at_load_is_a_structural_change(string folder, string name)
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var tree = LoadedTree.Create();
		var synchronizer = new DiskSynchronizer();

		synchronizer.Reset(
			tree.Solution,
			tree.SolutionPath,
			new EvaluationInputs(
				new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
				{
					[tree.ProjectPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				},
				[]));

		var before = await synchronizer.AbsorbNewAsync(tree.Solution, [], token);
		Assert.False(before.StructuralChange, "nothing has appeared yet");

		await File.WriteAllTextAsync(tree.PathTo(folder, name), "<Project />", token);

		var after = await synchronizer.AbsorbNewAsync(tree.Solution, [], token);
		Assert.True(after.StructuralChange, $"{name} appearing changes how the project evaluates");
	}

	/// <summary>A solution file and one project on disk, loaded into an ad hoc workspace.</summary>
	private sealed class LoadedTree : IDisposable
	{
		private readonly string _root;
		private readonly AdhocWorkspace _workspace;

		private LoadedTree(string root, AdhocWorkspace workspace, string solutionPath, string projectPath)
		{
			_root = root;
			_workspace = workspace;
			SolutionPath = solutionPath;
			ProjectPath = projectPath;
		}

		public string SolutionPath { get; }

		public string ProjectPath { get; }

		public Solution Solution => _workspace.CurrentSolution;

		public static LoadedTree Create()
		{
			var root = Directory.CreateTempSubdirectory("rosemcp-sync-").FullName;
			var solutionPath = Path.Combine(root, "Loaded.slnx");
			var projectPath = Path.Combine(root, "App", "App.csproj");

			Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
			File.WriteAllText(solutionPath, "<Solution />");
			File.WriteAllText(projectPath, "<Project />");

			var workspace = new AdhocWorkspace();
			workspace.AddProject(ProjectInfo.Create(
				ProjectId.CreateNewId(),
				VersionStamp.Create(),
				"App",
				"App",
				LanguageNames.CSharp,
				filePath: projectPath));

			return new LoadedTree(root, workspace, solutionPath, projectPath);
		}

		public string PathTo(params string[] parts) => Path.Combine([_root, .. parts]);

		public void Dispose()
		{
			_workspace.Dispose();
			Directory.Delete(_root, recursive: true);
		}
	}
}
