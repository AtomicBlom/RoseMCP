using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which layout a file is written in, and what decided it. Every writing tool asks this once for each
/// file it writes, so an answer read from the wrong place is the same wrong whitespace from every tool
/// at once -- which is why the order is pinned source by source here rather than through the tools.
/// <para>
/// A test that expects nothing to be declared gives its project an .editorconfig saying
/// <c>root = true</c> and nothing else, so no .editorconfig above the temporary directory on the
/// machine running it can answer in its place.
/// </para>
/// </summary>
public sealed class LayoutTests
{
	private const string NothingDeclared = "root = true\n";

	private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

	[Test]
	public async Task Takes_what_an_editorconfig_declares_over_what_the_file_does()
	{
		using var sandbox = Sandbox.Create().Given("root = true\n[*.cs]\nend_of_line = lf\nindent_style = space\nindent_size = 2\n");
		var document = sandbox.Document("A.cs", "class A\r\n{\r\n\tint a;\r\n}\r\n");

		var rules = await Whitespace.RulesForAsync(document, Token);

		rules.LineEnding.ShouldBe("\n");
		rules.IndentUnit.ShouldBe("  ");
		rules.LineEndingFrom.ShouldBe(LayoutSource.EditorConfig);
		rules.IndentFrom.ShouldBe(LayoutSource.EditorConfig);
	}

	/// <summary>
	/// The case .gitattributes exists for: a repository that settles its endings there and says nothing
	/// about them in .editorconfig. What git writes on checkout is the ending, not what the file happens to
	/// hold now.
	/// </summary>
	[Test]
	public async Task Takes_the_ending_gitattributes_gives_where_editorconfig_says_nothing()
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared).Repository("* text=auto eol=crlf\n");
		var document = sandbox.Document("A.cs", "class A\n{\n\tint a;\n}\n");

		var rules = await Whitespace.RulesForAsync(document, Token);

		rules.LineEnding.ShouldBe("\r\n");
		rules.LineEndingFrom.ShouldBe(LayoutSource.GitAttributes);
	}

	[Test]
	public async Task Takes_the_ending_editorconfig_declares_over_the_one_gitattributes_gives()
	{
		using var sandbox = Sandbox.Create().Given("root = true\n[*.cs]\nend_of_line = lf\n").Repository("* eol=crlf\n");
		var document = sandbox.Document("A.cs", "class A\r\n{\r\n}\r\n");

		var rules = await Whitespace.RulesForAsync(document, Token);

		rules.LineEnding.ShouldBe("\n");
		rules.LineEndingFrom.ShouldBe(LayoutSource.EditorConfig);
	}

	[Test]
	[Arguments("class A\r\n{\r\n\tint a;\r\n}\r\n", "\r\n")]
	[Arguments("class A\n{\n\tint a;\n}\n", "\n")]
	public async Task Keeps_the_ending_a_file_already_uses_where_nothing_declares_one(string text, string ending)
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared);

		var rules = await Whitespace.RulesForAsync(sandbox.Document("A.cs", text), Token);

		rules.LineEnding.ShouldBe(ending);
		rules.LineEndingFrom.ShouldBe(LayoutSource.File);
	}

	[Test]
	[Arguments("class A\r\n{\r\n\tint a;\r\n\tvoid B()\r\n\t{\r\n\t\tB();\r\n\t}\r\n}\r\n", "\t")]
	[Arguments("class A\r\n{\r\n  int a;\r\n  void B()\r\n  {\r\n    B();\r\n  }\r\n}\r\n", "  ")]
	[Arguments("class A\r\n{\r\n    int a;\r\n    void B()\r\n    {\r\n        B();\r\n    }\r\n}\r\n", "    ")]
	public async Task Keeps_the_indentation_a_file_already_uses_where_nothing_declares_one(string text, string unit)
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared);

		var rules = await Whitespace.RulesForAsync(sandbox.Document("A.cs", text), Token);

		rules.IndentUnit.ShouldBe(unit);
		rules.IndentFrom.ShouldBe(LayoutSource.File);
	}

	/// <summary>
	/// The lines inside a multi-line literal are the string's, and a tab-indented file holding one long
	/// block of text written against the margin with spaces is still a tab-indented file.
	/// </summary>
	[Test]
	public async Task Reads_indentation_from_the_code_and_not_from_inside_a_literal()
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared);

		var text = "class A\r\n{\r\n\tconst string S = @\"\r\n    one\r\n    two\r\n    three\r\n    four\r\n\";\r\n\tint a;\r\n}\r\n";

		var rules = await Whitespace.RulesForAsync(sandbox.Document("A.cs", text), Token);

		rules.IndentUnit.ShouldBe("\t");
	}

	/// <summary>
	/// A project the design-time build never described has no .editorconfig as far as Roslyn knows, and
	/// the one on disk is what dotnet format will read. It is read here too.
	/// </summary>
	[Test]
	public async Task Reads_the_editorconfig_on_disk_for_a_project_never_given_it()
	{
		using var sandbox = Sandbox.Create().OnDisk(".editorconfig", "root = true\n[*.cs]\nindent_style = tab\nend_of_line = crlf\n");
		var document = sandbox.Document("Nested/A.cs", "class A\n{\n    int a;\n}\n");

		var rules = await Whitespace.RulesForAsync(document, Token);

		rules.IndentUnit.ShouldBe("\t");
		rules.LineEnding.ShouldBe("\r\n");
		rules.IndentFrom.ShouldBe(LayoutSource.EditorConfig);
		rules.LineEndingFrom.ShouldBe(LayoutSource.EditorConfig);
	}

	/// <summary>
	/// Where Roslyn was given an .editorconfig covering the file it read the same files the disk holds,
	/// so its reading is the answer and the disk is not asked again.
	/// </summary>
	[Test]
	public async Task Takes_the_editorconfig_roslyn_was_given_over_the_disk()
	{
		using var sandbox = Sandbox.Create()
			.Given("root = true\n[*.cs]\nindent_style = space\nindent_size = 4\n")
			.OnDisk(".editorconfig", "root = true\n[*.cs]\nindent_style = tab\n");

		var rules = await Whitespace.RulesForAsync(sandbox.Document("A.cs", "class A\r\n{\r\n}\r\n"), Token);

		rules.IndentUnit.ShouldBe("    ");
	}

	/// <summary>
	/// A folder that held no source when the project was built has an .editorconfig the project was never given,
	/// below one it was. Roslyn's reading has the outer file's answer, and the nearer file on disk is the one a
	/// build reads once the new file is in that folder, so it decides.
	/// </summary>
	[Test]
	public async Task Takes_a_nearer_editorconfig_the_project_was_never_given_over_the_one_it_was()
	{
		const string Outer = "root = true\n[*.cs]\nindent_style = space\nindent_size = 4\n";

		using var sandbox = Sandbox.Create()
			.Given(Outer)
			.OnDisk(".editorconfig", Outer)
			.OnDisk("Rules/.editorconfig", "[*.cs]\nindent_style = tab\n");

		var rules = await Whitespace.RulesForAsync(sandbox.Document("Rules/A.cs", "class A\r\n{\r\n}\r\n"), Token);

		rules.IndentUnit.ShouldBe("\t");
		rules.IndentFrom.ShouldBe(LayoutSource.EditorConfig);
	}

	/// <summary>
	/// A new file has no text of its own to read, and the code a caller supplied for it is no witness:
	/// composed for a JSON argument it is LF and indented however the caller typed it. The files nearest
	/// it say how this repository is laid out.
	/// </summary>
	[Test]
	public async Task Takes_a_new_file_s_layout_from_the_files_beside_it()
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared);
		sandbox.Document("A.cs", "class A\r\n{\r\n\tint a;\r\n}\r\n");
		sandbox.Document("B.cs", "class B\r\n{\r\n\tint b;\r\n}\r\n");

		var rules = await Whitespace.RulesForNewAsync(sandbox.Project, sandbox.Path("C.cs"), from: null, Token);

		rules.LineEnding.ShouldBe("\r\n");
		rules.IndentUnit.ShouldBe("\t");
		rules.LineEndingFrom.ShouldBe(LayoutSource.Neighbours);
		rules.IndentFrom.ShouldBe(LayoutSource.Neighbours);
	}

	/// <summary>
	/// What the build writes under obj is laid out by its generator, and a generated file that happens to
	/// be nearest says nothing about how people here write theirs.
	/// </summary>
	[Test]
	public async Task Leaves_generated_files_out_of_a_new_file_s_neighbours()
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared);
		sandbox.Document("obj/Debug/A.g.cs", "class G\n{\n    int g;\n}\n");
		sandbox.Document("Code/B.cs", "class B\r\n{\r\n\tint b;\r\n}\r\n");

		var rules = await Whitespace.RulesForNewAsync(sandbox.Project, sandbox.Path("obj/Debug/C.cs"), from: null, Token);

		rules.LineEnding.ShouldBe("\r\n");
		rules.IndentUnit.ShouldBe("\t");
	}

	[Test]
	public async Task Falls_back_to_roslyn_s_defaults_where_nothing_says_anything()
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared);

		var rules = await Whitespace.RulesForNewAsync(sandbox.Project, sandbox.Path("A.cs"), from: null, Token);

		rules.IndentUnit.ShouldBe("    ");
		rules.LineEnding.ShouldBe(Environment.NewLine);
		rules.IndentFrom.ShouldBe(LayoutSource.Default);
		rules.LineEndingFrom.ShouldBe(LayoutSource.Default);
	}

	/// <summary>
	/// The damage this exists to stop. Roslyn's formatter, told nothing, sets the whitespace in front of the
	/// token after what it formats with its own four spaces, so an edit to one member of a tab-indented file
	/// re-indented the member after it.
	/// </summary>
	[Test]
	public async Task Formats_an_edit_without_re_indenting_the_member_after_it()
	{
		using var sandbox = Sandbox.Create().Given(NothingDeclared);
		var document = sandbox.Document("C.cs", "namespace N;\r\n\r\npublic class C\r\n{\r\n\tpublic int A() => 1;\r\n\r\n\tpublic int B() => 2;\r\n}\r\n");
		var rules = await Whitespace.RulesForAsync(document, Token);

		var root = (await document.GetSyntaxRootAsync(Token))!;
		var member = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
		var marker = new SyntaxAnnotation();
		var replacement = SyntaxFactory.ParseMemberDeclaration("public int A() => 10;")!.WithTriviaFrom(member).WithAdditionalAnnotations(marker);
		var edited = document.WithSyntaxRoot(root.ReplaceNode(member, replacement));

		var formatted = await Formatter.FormatAsync(edited, marker, await Whitespace.FormattingOptionsAsync(edited, rules, Token), Token);
		var text = (await formatted.GetTextAsync(Token)).ToString();

		text.ShouldContain("\r\n\tpublic int A() => 10;\r\n", Case.Sensitive);
		text.ShouldContain("\r\n\tpublic int B() => 2;\r\n", Case.Sensitive);
	}

	/// <summary>
	/// Where Roslyn was told how to indent, the formatter keeps its own reading of what it was told: only
	/// what it was not told is filled in.
	/// </summary>
	[Test]
	public async Task Fills_in_only_what_roslyn_was_not_told()
	{
		using var sandbox = Sandbox.Create().Given("root = true\n[*.cs]\nindent_style = space\n");
		var document = sandbox.Document("A.cs", "class A\r\n{\r\n\tint a;\r\n}\r\n");
		var rules = await Whitespace.RulesForAsync(document, Token);

		var options = await Whitespace.FormattingOptionsAsync(document, rules, Token);

		options.GetOption(FormattingOptions.UseTabs, LanguageNames.CSharp).ShouldBeFalse();
		options.GetOption(FormattingOptions.NewLine, LanguageNames.CSharp).ShouldBe("\r\n");
	}

	/// <summary>A project staged in a temporary directory, with whatever declares a layout around it.</summary>
	private sealed class Sandbox : IDisposable
	{
		private readonly AdhocWorkspace _workspace = new();
		private readonly ProjectId _project = ProjectId.CreateNewId();
		private Solution _solution;

		private Sandbox(string root)
		{
			Root = root;
			_solution = _workspace.CurrentSolution.AddProject(ProjectInfo.Create(
				_project, VersionStamp.Default, "P", "P", LanguageNames.CSharp, filePath: System.IO.Path.Combine(root, "P.csproj")));
		}

		private string Root { get; }

		public Project Project => _solution.GetProject(_project)!;

		public static Sandbox Create() => new(Directory.CreateTempSubdirectory("rosemcp-layout-").FullName);

		public string Path(string relative) =>
			System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

		/// <summary>An .editorconfig the project was given, as the design-time build gives one to a project it has described.</summary>
		public Sandbox Given(string editorConfig)
		{
			_solution = _solution.AddAnalyzerConfigDocument(
				DocumentId.CreateNewId(_project), ".editorconfig", SourceText.From(editorConfig), filePath: Path(".editorconfig"));

			return this;
		}

		/// <summary>A file on disk the project was never given.</summary>
		public Sandbox OnDisk(string relative, string content)
		{
			var path = Path(relative);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);

			return this;
		}

		/// <summary>A repository around the project, whose attributes say what they say.</summary>
		public Sandbox Repository(string attributes)
		{
			Directory.CreateDirectory(Path(".git"));

			return OnDisk(".gitattributes", attributes);
		}

		public Document Document(string relative, string text)
		{
			var id = DocumentId.CreateNewId(_project);
			_solution = _solution.AddDocument(id, System.IO.Path.GetFileName(relative), SourceText.From(text), filePath: Path(relative));

			return _solution.GetDocument(id)!;
		}

		public void Dispose()
		{
			_workspace.Dispose();
			Directory.Delete(Root, recursive: true);
		}
	}
}
