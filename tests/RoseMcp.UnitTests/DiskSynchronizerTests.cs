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

	/// <summary>
	/// A file the load saw and compiled into nothing is excluded on purpose -- a <c>Compile Remove</c>
	/// somewhere in the project's imports -- so absorbing it reports errors against a project that
	/// builds clean.
	/// </summary>
	[Test]
	public async Task A_file_the_load_compiled_into_no_project_is_left_alone()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var tree = LoadedTree.Create("Program.cs");

		var excluded = tree.PathTo("App", "Properties", "AssemblyInfo.cs");
		Directory.CreateDirectory(Path.GetDirectoryName(excluded)!);
		await File.WriteAllTextAsync(excluded, "[assembly: System.Reflection.AssemblyTitle(\"App\")]", token);

		var synchronizer = new DiskSynchronizer();
		synchronizer.Reset(tree.Solution, tree.SolutionPath, Evaluated(tree), token);

		var absorbed = await synchronizer.AbsorbNewAsync(tree.Solution, [], token);

		Assert.Empty(absorbed.Added);
		Assert.Empty(absorbed.NotInTheBuild);
	}

	/// <summary>
	/// The exclusion covers what the load looked at and nothing else, so a file written afterwards is
	/// still absorbed -- which is the whole reason this walk exists.
	/// </summary>
	[Test]
	public async Task A_file_written_after_the_load_is_still_absorbed()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var tree = LoadedTree.Create("Program.cs");

		var synchronizer = new DiskSynchronizer();
		synchronizer.Reset(tree.Solution, tree.SolutionPath, Evaluated(tree), token);

		var written = tree.PathTo("App", "Added.cs");
		await File.WriteAllTextAsync(written, "class Added;", token);

		var absorbed = await synchronizer.AbsorbNewAsync(tree.Solution, [], token);

		Assert.Single(absorbed.Added);
		Assert.Contains(written, absorbed.Added);
	}

	/// <summary>
	/// A project the build filled with no documents has said nothing about what it excludes, so its
	/// silence is not read as exclusion -- otherwise a project whose build failed could never absorb
	/// a file again.
	/// </summary>
	[Test]
	public async Task A_project_the_load_left_empty_still_absorbs()
	{
		var token = TestContext.Current!.Execution.CancellationToken;
		using var tree = LoadedTree.Create();

		var onDisk = tree.PathTo("App", "Program.cs");
		await File.WriteAllTextAsync(onDisk, "class Program;", token);

		var synchronizer = new DiskSynchronizer();
		synchronizer.Reset(tree.Solution, tree.SolutionPath, Evaluated(tree), token);

		var absorbed = await synchronizer.AbsorbNewAsync(tree.Solution, [], token);

		Assert.Contains(onDisk, absorbed.Added);
	}

	/// <summary>Every project evaluated, importing nothing.</summary>
	private static EvaluationInputs Evaluated(LoadedTree tree) =>
		new(
			new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
			{
				[tree.ProjectPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			},
			[]);

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

		/// <param name="compiled">
		/// Files written under the project and given to it as documents, standing for what a
		/// design-time build compiled. A tree with none of them stands for a project the build could
		/// not fill.
		/// </param>
		public static LoadedTree Create(params string[] compiled)
		{
			var root = Directory.CreateTempSubdirectory("rosemcp-sync-").FullName;
			var solutionPath = Path.Combine(root, "Loaded.slnx");
			var projectPath = Path.Combine(root, "App", "App.csproj");

			Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
			File.WriteAllText(solutionPath, "<Solution />");

			// SDK-style, so the default globs are what decides a new file's fate and the exclusion
			// recorded at load is what these tests actually exercise.
			File.WriteAllText(projectPath, """<Project Sdk="Microsoft.NET.Sdk" />""");

			var projectId = ProjectId.CreateNewId();
			var documents = new List<DocumentInfo>();

			foreach (var name in compiled)
			{
				var path = Path.Combine(Path.GetDirectoryName(projectPath)!, name);

				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, $"// {name}");

				documents.Add(DocumentInfo.Create(
					DocumentId.CreateNewId(projectId),
					Path.GetFileName(path),
					filePath: path));
			}

			var workspace = new AdhocWorkspace();
			workspace.AddProject(ProjectInfo.Create(
				projectId,
				VersionStamp.Create(),
				"App",
				"App",
				LanguageNames.CSharp,
				filePath: projectPath,
				documents: documents));

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
