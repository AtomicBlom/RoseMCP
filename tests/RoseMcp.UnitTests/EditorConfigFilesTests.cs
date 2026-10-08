using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which analyzer config files on disk a project was never given. The load fills in the ones above a project
/// with nothing to compile, and a write that lands where one is still missing says so rather than calling the
/// project clean, so a wrong answer here is either a project compiled under the wrong severities or a caveat
/// on every write in a repository that has nothing to warn about.
/// <para>
/// Each test looks only at files inside its own temporary directory, so an .editorconfig somewhere above the
/// temporary directory on the machine running it cannot decide the answer.
/// </para>
/// </summary>
public sealed class EditorConfigFilesTests
{
	/// <summary>
	/// A project built with nothing in it was given nothing, and the files in and above its directory are what
	/// a build gives it once a source is there -- both kinds, since a .globalconfig is discovered the same way.
	/// </summary>
	[Test]
	public void Finds_the_files_above_a_project_it_was_not_given()
	{
		using var sandbox = new Sandbox();
		var root = sandbox.OnDisk(".editorconfig", "root = true\n[*.cs]\nindent_style = tab\n");
		var global = sandbox.OnDisk(".globalconfig", "is_global = true\ndotnet_diagnostic.CS0168.severity = error\n");
		var own = sandbox.OnDisk("P/.editorconfig", "[*.cs]\nindent_size = 4\n");

		var missing = sandbox.Inside(EditorConfigFiles.MissingAbove(sandbox.Project, ConfigDiscovery.Both));

		missing.ShouldBe([own, root, global], ignoreOrder: true);
	}

	/// <summary>A file the project was given is not missing, and neither is a kind its build does not discover.</summary>
	[Test]
	public void Leaves_out_what_was_given_and_what_the_project_does_not_discover()
	{
		using var sandbox = new Sandbox();
		var root = sandbox.OnDisk(".editorconfig", "root = true\n");
		sandbox.OnDisk(".globalconfig", "is_global = true\n");
		sandbox.Given(root);

		sandbox.Inside(EditorConfigFiles.MissingAbove(sandbox.Project, ConfigDiscovery.EditorConfig)).ShouldBeEmpty();
	}

	/// <summary>
	/// A folder that held no source when the project was built: its .editorconfig was never given, while the one
	/// above, given because the project's other sources sit under it, was. Only the nested one is unread.
	/// </summary>
	[Test]
	public void Names_a_nested_editorconfig_the_project_was_never_given()
	{
		using var sandbox = new Sandbox();
		var root = sandbox.OnDisk(".editorconfig", "root = true\n");
		var nested = sandbox.OnDisk("P/Rules/.editorconfig", "[*.cs]\ndotnet_diagnostic.CS0168.severity = error\n");
		sandbox.Given(root);

		sandbox.Inside(EditorConfigFiles.NotGiven(sandbox.Project, sandbox.Path("P/Rules/A.cs"))).ShouldBe([nested]);
		sandbox.Inside(EditorConfigFiles.NotGiven(sandbox.Project, sandbox.Path("P/A.cs"))).ShouldBeEmpty();
	}

	/// <summary>
	/// An .editorconfig above one saying <c>root = true</c> applies to nothing below it, so leaving it out
	/// changes no answer. A .globalconfig applies to the whole project wherever it sits, so it still counts.
	/// </summary>
	[Test]
	public void Stops_at_a_root_editorconfig_but_not_for_a_globalconfig()
	{
		using var sandbox = new Sandbox();
		sandbox.OnDisk(".editorconfig", "[*.cs]\nindent_style = space\n");
		var global = sandbox.OnDisk(".globalconfig", "is_global = true\n");
		var rooted = sandbox.OnDisk("P/.editorconfig", "# the repository's own\nroot = true\n[*.cs]\nindent_style = tab\n");

		var unread = sandbox.Inside(EditorConfigFiles.NotGiven(sandbox.Project, sandbox.Path("P/A.cs")));

		unread.ShouldBe([rooted, global], ignoreOrder: true);
	}

	/// <summary>Nothing on disk speaks for the file, so there is nothing to warn about.</summary>
	[Test]
	public void Has_nothing_to_say_where_nothing_is_on_disk()
	{
		using var sandbox = new Sandbox();

		sandbox.Inside(EditorConfigFiles.NotGiven(sandbox.Project, sandbox.Path("P/A.cs"))).ShouldBeEmpty();
		sandbox.Inside(EditorConfigFiles.MissingAbove(sandbox.Project, ConfigDiscovery.Both)).ShouldBeEmpty();
	}

	/// <summary>A project in a temporary directory, at <c>P/P.csproj</c> under it, given only what a test gives it.</summary>
	private sealed class Sandbox : IDisposable
	{
		private readonly AdhocWorkspace _workspace = new();
		private readonly ProjectId _project = ProjectId.CreateNewId();
		private readonly string _root = Directory.CreateTempSubdirectory("rosemcp-configs-").FullName;
		private Solution _solution;

		public Sandbox()
		{
			Directory.CreateDirectory(Path("P"));
			_solution = _workspace.CurrentSolution.AddProject(ProjectInfo.Create(
				_project, VersionStamp.Default, "P", "P", LanguageNames.CSharp, filePath: Path("P/P.csproj")));
		}

		public Project Project => _solution.GetProject(_project)!;

		public string Path(string relative) =>
			System.IO.Path.Combine(_root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

		public string OnDisk(string relative, string content)
		{
			var path = Path(relative);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);

			return path;
		}

		/// <summary>Gives the project a file, as the design-time build gives one above a source it compiles.</summary>
		public void Given(string path) =>
			_solution = _solution.AddAnalyzerConfigDocument(
				DocumentId.CreateNewId(_project), System.IO.Path.GetFileName(path), SourceText.From(File.ReadAllText(path)), filePath: path);

		/// <summary>The paths inside this sandbox, so whatever the machine keeps above the temporary directory is left out.</summary>
		public IReadOnlyList<string> Inside(IEnumerable<string> paths) =>
			[.. paths.Where(path => path.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];

		public void Dispose()
		{
			_workspace.Dispose();
			Directory.Delete(_root, recursive: true);
		}
	}
}
