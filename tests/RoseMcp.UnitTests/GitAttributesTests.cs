namespace RoseMcp.UnitTests;

/// <summary>
/// What a repository's attributes say a file's lines end with once git has checked it out.
/// <para>
/// Every write asks this when .editorconfig says nothing about endings, so a wrong answer here is a
/// file written in the wrong endings by every tool at once -- and a pattern read more broadly than git
/// reads it is exactly that. The rules pinned are the ones a repository actually writes: a catch-all
/// with an <c>eol</c>, a narrower line overriding it, a subdirectory overriding the top level, and
/// the attributes that take a file out of conversion altogether.
/// </para>
/// </summary>
public sealed class GitAttributesTests
{
	[Test]
	public void Reads_the_ending_a_catch_all_line_gives()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "* text=auto eol=crlf");

		GitAttributes.LineEndingFor(repository.Path("src/A.cs")).ShouldBe("\r\n");
	}

	/// <summary>
	/// Text without an eol is converted to whatever each machine's core.eol or core.autocrlf says, so the
	/// attributes have no answer of their own and the file's endings are the better witness.
	/// </summary>
	[Test]
	[Arguments("* text=auto")]
	[Arguments("* text")]
	[Arguments("*.cs diff=csharp")]
	public void Has_no_answer_where_the_attributes_leave_the_ending_to_the_machine(string line)
	{
		using var repository = Repository.Create();
		repository.Attributes("", line);

		GitAttributes.LineEndingFor(repository.Path("A.cs")).ShouldBeNull();
	}

	/// <summary>
	/// A file git does not convert keeps the endings it was committed with, whatever an eol beside it
	/// says, because git reads eol only for a file it treats as text.
	/// </summary>
	[Test]
	[Arguments("*.cs -text")]
	[Arguments("*.cs binary")]
	[Arguments("*.cs -crlf")]
	public void Has_no_answer_for_a_file_git_does_not_convert(string line)
	{
		using var repository = Repository.Create();
		repository.Attributes("", "* eol=crlf", line);

		GitAttributes.LineEndingFor(repository.Path("A.cs")).ShouldBeNull();
	}

	[Test]
	public void Takes_the_later_of_two_lines_that_match()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "*.cs eol=lf", "* eol=crlf");

		GitAttributes.LineEndingFor(repository.Path("A.cs")).ShouldBe("\r\n");
	}

	[Test]
	public void Lets_a_bang_return_an_attribute_to_unspecified()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "* eol=crlf", "*.cs !eol");

		GitAttributes.LineEndingFor(repository.Path("A.cs")).ShouldBeNull();
	}

	[Test]
	public void Lets_a_deeper_file_override_the_top_level_one()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "* eol=crlf");
		repository.Attributes("src", "*.cs eol=lf");

		GitAttributes.LineEndingFor(repository.Path("src/deep/A.cs")).ShouldBe("\n");
		GitAttributes.LineEndingFor(repository.Path("B.cs")).ShouldBe("\r\n");
	}

	/// <summary>
	/// The top of the order, above every .gitattributes: a machine's own override for the repository,
	/// kept under the git directory where no commit carries it.
	/// </summary>
	[Test]
	public void Lets_info_attributes_override_every_file()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "* eol=lf");
		repository.Attributes("src", "* eol=lf");
		repository.Info("* eol=crlf");

		GitAttributes.LineEndingFor(repository.Path("src/A.cs")).ShouldBe("\r\n");
	}

	/// <summary>
	/// A linked worktree's git directory is its own, and info/attributes is not in it: it lives in the
	/// directory every worktree of the repository shares, which the linked one names in commondir.
	/// </summary>
	[Test]
	public void Reads_info_attributes_from_a_linked_worktree_s_common_directory()
	{
		using var repository = Repository.Create();
		repository.Info("* eol=crlf");

		var worktree = repository.Worktree("wt");

		GitAttributes.LineEndingFor(Path.Combine(worktree, "src", "A.cs")).ShouldBe("\r\n");
	}

	/// <summary>
	/// A slash anywhere but the end anchors a pattern to the directory of the file holding it, and a
	/// single star stops at a slash, so this reaches the files directly in src and none below.
	/// </summary>
	[Test]
	public void Anchors_a_pattern_with_a_slash_to_its_own_directory()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "src/*.cs eol=lf");

		GitAttributes.LineEndingFor(repository.Path("src/A.cs")).ShouldBe("\n");
		GitAttributes.LineEndingFor(repository.Path("src/deep/A.cs")).ShouldBeNull();
		GitAttributes.LineEndingFor(repository.Path("other/src/A.cs")).ShouldBeNull();
	}

	[Test]
	public void Matches_a_pattern_without_a_slash_at_any_depth()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "*.cs eol=lf");

		GitAttributes.LineEndingFor(repository.Path("a/b/c/A.cs")).ShouldBe("\n");
		GitAttributes.LineEndingFor(repository.Path("a/b/c/A.csproj")).ShouldBeNull();
	}

	/// <summary>
	/// Each pattern against a path git matches it with and one it does not, taken from what
	/// <c>git check-attr</c> answers rather than from a reading of the documentation.
	/// </summary>
	[Test]
	[Arguments("src/**/*.cs", "src/A.cs", true)]
	[Arguments("src/**/*.cs", "src/a/b/A.cs", true)]
	[Arguments("src/**/*.cs", "tests/A.cs", false)]
	[Arguments("**/gen/*.cs", "gen/A.cs", true)]
	[Arguments("**/gen/*.cs", "x/y/gen/A.cs", true)]
	[Arguments("**/gen/*.cs", "x/gen/deep/A.cs", false)]
	[Arguments("gen/**", "gen/deep/A.cs", true)]
	[Arguments("/A.cs", "A.cs", true)]
	[Arguments("/A.cs", "src/A.cs", false)]
	[Arguments("A?.cs", "AB.cs", true)]
	[Arguments("A?.cs", "A.cs", false)]
	[Arguments("[ab].cs", "b.cs", true)]
	[Arguments("[ab].cs", "c.cs", false)]
	[Arguments("[!a].cs", "b.cs", true)]
	[Arguments("[!a].cs", "a.cs", false)]
	[Arguments("[a-c].cs", "b.cs", true)]
	public void Matches_what_git_matches(string pattern, string path, bool matches)
	{
		using var repository = Repository.Create();
		repository.Attributes("", $"{pattern} eol=lf");

		GitAttributes.LineEndingFor(repository.Path(path)).ShouldBe(matches ? "\n" : null, $"{pattern} against {path}");
	}

	/// <summary>
	/// A pattern naming a directory never reaches the files inside it: attributes are not inherited, which
	/// is the one place gitattributes parts company with gitignore.
	/// </summary>
	[Test]
	public void Reaches_nothing_inside_a_directory_a_pattern_names()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "src/ eol=lf");

		GitAttributes.LineEndingFor(repository.Path("src/A.cs")).ShouldBeNull();
	}

	/// <summary>
	/// Git refuses a negative pattern in an attributes file and skips the line, so it must not read as a
	/// pattern matching everything else.
	/// </summary>
	[Test]
	public void Skips_a_negative_pattern_as_git_does()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "!*.txt eol=lf");

		GitAttributes.LineEndingFor(repository.Path("A.cs")).ShouldBeNull();
	}

	[Test]
	public void Reads_a_quoted_pattern()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "\"My File.cs\" eol=lf");

		GitAttributes.LineEndingFor(repository.Path("My File.cs")).ShouldBe("\n");
	}

	/// <summary>
	/// Git matches under core.ignorecase, which clone sets on a file system that ignores case, so on
	/// Windows a pattern written in capitals still names the file.
	/// </summary>
	[Test]
	public void Ignores_case_where_the_file_system_does()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "*.CS eol=lf");

		var ignoresCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

		GitAttributes.LineEndingFor(repository.Path("A.cs")).ShouldBe(ignoresCase ? "\n" : null);
	}

	/// <summary>
	/// Under core.ignorecase git folds the path and the pattern's plain characters, but not the members of
	/// a bracket expression nor a character after a backslash, and it gives only a range a second chance
	/// with the path's capital. So on Windows a capital written alone in a set names no file, while a range
	/// of capitals names both cases -- the answers <c>git check-attr</c> gives, pinned here because reading
	/// the set case-insensitively instead would put files under an <c>eol</c> git does not give them.
	/// </summary>
	[Test]
	[Arguments("[AB].cs", "B.cs", false)]
	[Arguments("[AB].cs", "b.cs", false)]
	[Arguments("[ab].cs", "B.cs", true)]
	[Arguments("[!a].cs", "A.cs", false)]
	[Arguments("[A-Z].cs", "b.cs", true)]
	[Arguments("[a-c].cs", "B.cs", true)]
	[Arguments("\\A.cs", "A.cs", false)]
	[Arguments("\\a.cs", "A.cs", true)]
	[Arguments("Ab*.cs", "aBc.cs", true)]
	public void Folds_case_where_git_folds_it_and_nowhere_else(string pattern, string path, bool whereCaseIsIgnored)
	{
		if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;

		using var repository = Repository.Create();
		repository.Attributes("", $"{pattern} eol=lf");

		GitAttributes.LineEndingFor(repository.Path(path)).ShouldBe(whereCaseIsIgnored ? "\n" : null, $"{pattern} against {path}");
	}

	/// <summary>
	/// A macro stands for the attributes it was defined with, which is how a repository names a policy
	/// once and applies it by name -- but only the top-level file may define one, and git ignores a
	/// definition anywhere else.
	/// </summary>
	[Test]
	public void Expands_a_macro_the_top_level_file_defines()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "[attr]windows text eol=crlf", "*.cs windows");

		GitAttributes.LineEndingFor(repository.Path("src/A.cs")).ShouldBe("\r\n");
	}

	[Test]
	public void Ignores_a_macro_defined_below_the_top_level()
	{
		using var repository = Repository.Create();
		repository.Attributes("src", "[attr]windows text eol=crlf", "*.cs windows");

		GitAttributes.LineEndingFor(repository.Path("src/A.cs")).ShouldBeNull();
	}

	/// <summary>crlf=input is older than eol, and means what eol=lf means: LF in the working tree.</summary>
	[Test]
	public void Reads_the_older_crlf_attribute()
	{
		using var repository = Repository.Create();
		repository.Attributes("", "*.cs crlf=input");

		GitAttributes.LineEndingFor(repository.Path("A.cs")).ShouldBe("\n");
	}

	/// <summary>A staged checkout with a git directory of its own, which goes away with the test.</summary>
	private sealed class Repository : IDisposable
	{
		private Repository(string root) => Root = root;

		private string Root { get; }

		public static Repository Create()
		{
			var root = Directory.CreateTempSubdirectory("rosemcp-attributes-").FullName;
			Directory.CreateDirectory(System.IO.Path.Combine(root, ".git"));

			return new Repository(root);
		}

		/// <summary>A path in the checkout, which need not exist: nothing here reads the file itself.</summary>
		public string Path(string relative) =>
			System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

		public void Attributes(string directory, params string[] lines)
		{
			var at = Path(directory);
			Directory.CreateDirectory(at);
			File.WriteAllLines(System.IO.Path.Combine(at, ".gitattributes"), lines);
		}

		public void Info(params string[] lines)
		{
			var info = Path(".git/info");
			Directory.CreateDirectory(info);
			File.WriteAllLines(System.IO.Path.Combine(info, "attributes"), lines);
		}

		/// <summary>
		/// A linked worktree beside the checkout, laid out as git lays one out: a .git file naming a
		/// directory under the repository's worktrees folder, which names the shared directory in
		/// commondir.
		/// </summary>
		public string Worktree(string name)
		{
			var linked = Path($".git/worktrees/{name}");
			Directory.CreateDirectory(linked);
			File.WriteAllText(System.IO.Path.Combine(linked, "commondir"), "../..\n");

			var worktree = Path(name);
			Directory.CreateDirectory(worktree);
			File.WriteAllText(System.IO.Path.Combine(worktree, ".git"), $"gitdir: {linked}\n");

			return worktree;
		}

		public void Dispose() => Directory.Delete(Root, recursive: true);
	}
}
