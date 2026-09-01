using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker.Tests;

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
	};

	[Fact]
	public void Gives_every_line_the_ending_the_rules_ask_for()
	{
		var source = string.Join(string.Empty, "class C" + Crlf, "{" + Lf, "\tint Value;" + Lf, "}");

		var result = Apply(source, Strict);

		Assert.DoesNotContain(Lf, StripCrlf(result), StringComparison.Ordinal);
		Assert.EndsWith(Crlf, result, StringComparison.Ordinal);
	}

	[Fact]
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
	[Fact]
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

	[Fact]
	public void Leaves_a_verbatim_string_alone_too()
	{
		var source = string.Join(Lf, "class C", "{", "\tconst string Text = @\"first", "second\";", "}");

		var result = Apply(source, Strict);

		Assert.Contains("first" + Lf + "second", result, StringComparison.Ordinal);
	}

	[Fact]
	public void Changes_nothing_when_the_file_already_obeys_the_rules()
	{
		var source = "class C" + Crlf + "{" + Crlf + "\tint Value;" + Crlf + "}" + Crlf;

		Assert.Equal(source, Apply(source, Strict));
	}

	[Theory]
	[InlineData("a\r\nb\r\nc\n", "\r\n")]
	[InlineData("a\nb\nc\r\n", "\n")]
	[InlineData("a\rb\rc\r", "\r")]
	public void Reads_the_ending_a_file_mostly_uses(string source, string expected)
	{
		// Which is the fallback when .editorconfig says nothing: matching the file is what keeps a
		// format from showing up as a whole-file diff.
		Assert.Equal(expected, Whitespace.Dominant(SourceText.From(source)));
	}

	private static string Apply(string source, WhitespaceRules rules)
	{
		var tree = CSharpSyntaxTree.ParseText(source);
		var text = SourceText.From(source);

		return Whitespace.Apply(tree.GetRoot(TestContext.Current.CancellationToken), text, rules).ToString();
	}

	private static string StripCrlf(string text) => text.Replace(Crlf, string.Empty, StringComparison.Ordinal);
}
