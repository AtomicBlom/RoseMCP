using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.UnitTests;

/// <summary>
/// The whitespace pass that runs after Roslyn's formatter, which is the half that fixes line
/// endings. Measured need: formatting a four-space, LF-terminated file produces tabs and CRLF on
/// every line the formatter reindents and leaves the rest exactly as they arrived, so the result has
/// mixed endings -- which is what a repository escalating IDE0055 fails the build over.
/// </summary>
public sealed class WhitespaceTests
{
	private const string Lf = "\n";
	private const string Crlf = "\r\n";

	private static readonly WhitespaceRules Strict = new()
	{
		LineEnding = Crlf,
		TrimTrailingWhitespace = true,
		InsertFinalNewline = true,
		IndentUnit = "\t",
	};

	[Test]
	public void Gives_every_line_the_ending_the_rules_ask_for()
	{
		var source = string.Join(string.Empty, "class C" + Crlf, "{" + Lf, "\tint Value;" + Lf, "}");

		var result = Apply(source, Strict);

		Assert.DoesNotContain(Lf, StripCrlf(result), StringComparison.Ordinal);
		Assert.EndsWith(Crlf, result, StringComparison.Ordinal);
	}

	[Test]
	public void Trims_trailing_whitespace_and_adds_the_final_newline()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tint Value;   " + Crlf + "}";

		var result = Apply(source, Strict);

		Assert.Contains("\tint Value;" + Crlf, result, StringComparison.Ordinal);
		Assert.DoesNotContain("   " + Crlf, result, StringComparison.Ordinal);
		Assert.EndsWith("}" + Crlf, result, StringComparison.Ordinal);
	}

	/// <summary>
	/// The one case where whitespace is content rather than layout. A newline inside a raw or verbatim
	/// literal is part of the value, and a raw literal's indentation decides how much is stripped from
	/// every line of it -- so normalising in there changes what the program does, silently.
	/// </summary>
	[Test]
	public void Leaves_a_multi_line_raw_string_exactly_as_it_was()
	{
		var literal = "\"\"\"" + Lf + "\t\t\tfirst  " + Lf + "\t\t\tsecond" + Lf + "\t\t\t\"\"\"";
		var source = string.Join(
			Lf,
			"class C",
			"{",
			"\tconst string Text = " + literal + ";",
			"}");

		var result = Apply(source, Strict);

		// Its interior newlines are still bare, and the trailing spaces inside it are still there.
		Assert.Contains("first  " + Lf, result, StringComparison.Ordinal);
		Assert.Contains("second" + Lf, result, StringComparison.Ordinal);

		// While the code around it was normalised.
		Assert.StartsWith("class C" + Crlf + "{" + Crlf, result, StringComparison.Ordinal);
		Assert.EndsWith("}" + Crlf, result, StringComparison.Ordinal);
	}

	[Test]
	public void Leaves_a_verbatim_string_alone_too()
	{
		var source = string.Join(Lf, "class C", "{", "\tconst string Text = @\"first", "second\";", "}");

		var result = Apply(source, Strict);

		Assert.Contains("first" + Lf + "second", result, StringComparison.Ordinal);
	}

	[Test]
	public void Changes_nothing_when_the_file_already_obeys_the_rules()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tint Value;" + Crlf + "}" + Crlf;

		Assert.Equal(source, Apply(source, Strict));
	}

	[Test]
	[Arguments("a\r\nb\r\nc\n", "\r\n")]
	[Arguments("a\nb\nc\r\n", "\n")]
	[Arguments("a\rb\rc\r", "\r")]
	public void Reads_the_ending_a_file_mostly_uses(string source, string expected)
	{
		// Which is the fallback when .editorconfig says nothing: matching the file is what keeps a
		// format from showing up as a whole-file diff.
		Assert.Equal(expected, Whitespace.Dominant(SourceText.From(source)));
	}

	/// <summary>
	/// The literal the whitespace pass deliberately will not touch is the one that then fails
	/// dotnet format, so the least it can do is say where it is.
	/// </summary>
	[Test]
	public void Reports_a_multi_line_literal_whose_endings_are_not_the_files()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tconst string Text = @\"one" + Lf + "two\";" + Crlf + "}" + Crlf;

		Assert.Equal([3], Disagreeing(source));
	}

	/// <summary>
	/// Any disagreeing ending counts, not the literal's dominant one. A hand splice leaves a literal
	/// that is mostly the file's endings with one line that is not, and that line is exactly what
	/// dotnet format fails on -- asking which ending it mostly uses would call this clean.
	/// </summary>
	[Test]
	public void Reports_a_literal_that_mostly_agrees_and_partly_does_not()
	{
		var source = "class C" + Crlf + "{" + Crlf
			+ "\tconst string Text = @\"one" + Crlf + "two" + Lf + "three" + Crlf + "four\";" + Crlf
			+ "}" + Crlf;

		Assert.Equal([3], Disagreeing(source));
	}

	[Test]
	public void Says_nothing_about_a_literal_written_with_the_files_own_endings()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tconst string Text = @\"one" + Crlf + "two\";" + Crlf + "}" + Crlf;

		Assert.Empty(Disagreeing(source));
	}

	/// <summary>A single-line literal cannot hold a line ending, so it can never disagree about one.</summary>
	[Test]
	public void Says_nothing_about_a_single_line_literal()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tconst string Text = \"one\";" + Crlf + "}" + Crlf;

		Assert.Empty(Disagreeing(source));
	}

	/// <summary>
	/// A caller reporting on its own edit is asking about what it wrote, not about the file it landed
	/// in -- a member replacement that warned about a literal four hundred lines away would be
	/// blaming this change for something it did not do.
	/// </summary>
	[Test]
	public void Ignores_a_disagreeing_literal_outside_the_span_asked_about()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tconst string Text = @\"one" + Lf + "two\";" + Crlf + "}" + Crlf;

		Assert.Empty(Disagreeing(source, SourceText.From(source).Lines[0].SpanIncludingLineBreak));
	}

	/// <summary>
	/// A raw literal's closing delimiter takes the file's ending, and so does the line its opening
	/// one is on. Neither break is content: a raw literal's value begins after the break that
	/// follows the opening quotes and stops before the one in front of the closing quotes, so both
	/// are the literal's punctuation.
	/// <para>
	/// Left as they arrived, a literal written with LF put a lone LF into a CRLF file on the two
	/// lines that were never part of the string -- which dotnet format rejects, on a line where the
	/// obvious fix does not change what the program says. The content between them stays exactly as
	/// it is, which is asserted on the bytes rather than the shape.
	/// </para>
	/// </summary>
	[Test]
	public void Gives_a_raw_literal_delimiter_the_file_ending_and_leaves_the_content_alone()
	{
		var source = "class C" + Crlf
			+ "{" + Crlf
			+ "\tconst string Text = \"\"\"" + Lf
			+ "\t\tone" + Lf
			+ "\t\ttwo" + Lf
			+ "\t\t\"\"\";" + Lf
			+ "}" + Crlf;

		var result = Apply(source, Strict);

		// The two content lines keep their line feeds: those are what the string says.
		Assert.Contains("\t\tone" + Lf + "\t\ttwo" + Lf, result, StringComparison.Ordinal);

		// Both delimiter lines take the file's ending.
		Assert.Contains("= \"\"\"" + Crlf, result, StringComparison.Ordinal);
		Assert.Contains("\"\"\";" + Crlf, result, StringComparison.Ordinal);
	}

	/// <summary>
	/// A verbatim literal has no delimiter line to normalise: every break inside it is part of its
	/// value, the first one included, so the whole of it is left alone.
	/// </summary>
	[Test]
	public void Leaves_every_line_of_a_verbatim_literal_alone()
	{
		var source = "class C" + Crlf
			+ "{" + Crlf
			+ "\tconst string Text = @\"one" + Lf
			+ "two\";" + Lf
			+ "}" + Crlf;

		var result = Apply(source, Strict);

		Assert.Contains("@\"one" + Lf + "two\";", result, StringComparison.Ordinal);
	}

	/// <summary>
	/// The sentence itself, since two tools now share it and a caller who runs both has to be told the
	/// same thing by each. It names the line so the literal can be found, and says why nothing was
	/// rewritten, because the obvious fix changes what the program says.
	/// </summary>
	[Test]
	public void Says_which_literals_hold_endings_the_file_does_not_use()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tconst string T = \"\"\"" + Crlf
			+ "first" + Lf + "second" + Lf + "\"\"\";" + Crlf + "}" + Crlf;

		var notice = Notice(source, "Described.cs");

		Assert.NotNull(notice);
		Assert.StartsWith("Described.cs: the multi-line string at line 3", notice, StringComparison.Ordinal);
		Assert.Contains("line endings the file does not use", notice, StringComparison.Ordinal);
		Assert.Contains("dotnet format will still ask for them", notice, StringComparison.Ordinal);
	}

	/// <summary>Nothing to say about a file whose literals agree with it, so the notice means something.</summary>
	[Test]
	public void Says_nothing_about_a_file_whose_literals_agree_with_it()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tconst string T = \"\"\"" + Crlf
			+ "first" + Crlf + "second" + Crlf + "\"\"\";" + Crlf + "}" + Crlf;

		Assert.Null(Notice(source, "Agreed.cs"));
	}

	private static IReadOnlyList<int> Disagreeing(string source, TextSpan? within = null)
	{
		var tree = CSharpSyntaxTree.ParseText(source);
		var text = SourceText.From(source);

		return Whitespace.LiteralsDisagreeingWith(
			tree.GetRoot(TestContext.Current!.Execution.CancellationToken), text, Strict, within);
	}

	private static string? Notice(string source, string name)
	{
		var tree = CSharpSyntaxTree.ParseText(source);

		return Whitespace.LiteralEndingNotice(tree.GetRoot(), SourceText.From(source), Strict, name);
	}

	private static string Apply(string source, WhitespaceRules rules)
	{
		var tree = CSharpSyntaxTree.ParseText(source);
		var text = SourceText.From(source);

		return Whitespace.Apply(tree.GetRoot(TestContext.Current!.Execution.CancellationToken), text, rules).ToString();
	}

	private static string StripCrlf(string text) => text.Replace(Crlf, string.Empty, StringComparison.Ordinal);
}
