using Microsoft.CodeAnalysis.Text;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// What an edit is reported to have introduced, in a file that had errors of the same kind already --
/// which is where matching on anything short of position lists the wrong one.
/// </summary>
public sealed class DiagnosticDeltaTests
{
	private const string ShellPath = @"C:\repo\Shell.xaml.cs";

	private const string Ambiguous = "Ambiguity between 'Shell.BottomBarHost' and 'Shell.BottomBarHost'";

	private static readonly SourceText Before = SourceText.From(string.Join("\n",
		"class Shell",                 // 1
		"{",                           // 2
		"\tShell()",                   // 3
		"\t{",                         // 4
		"\t}",                         // 5
		"",                            // 6
		"\tvoid A() => Use(BottomBarHost);",   // 7
		"\tvoid B() => Use(BottomBarHost);",   // 8
		"}"));

	/// <summary>
	/// A new use of the ambiguous name goes into the constructor. Three errors after, two before, all
	/// with the same message: the one listed has to be the one on the new line, not the one that was on
	/// line 8 and is now on line 9.
	/// </summary>
	[Test]
	public void Lists_the_error_on_the_new_line_rather_than_one_that_moved()
	{
		var after = Insert(Before, line: 4, "\t\tUse(BottomBarHost);");
		var movement = TextMovement.Of(ShellPath, Before, after);

		var (introduced, resolved) = DiagnosticDelta.Compare(
			[Error(7, 18), Error(8, 18)],
			[Error(5, 7), Error(8, 18), Error(9, 18)],
			movement);

		var entry = introduced.ShouldHaveSingleItem();
		entry.Line.ShouldBe(5);
		resolved.ShouldBe(0);
	}

	/// <summary>Lines that only moved are neither introduced nor resolved.</summary>
	[Test]
	public void Treats_errors_below_an_insertion_as_the_same_errors()
	{
		var after = Insert(Before, line: 4, "\t\tvar a = 1;\n\t\tvar b = 2;");
		var movement = TextMovement.Of(ShellPath, Before, after);

		var (introduced, resolved) = DiagnosticDelta.Compare(
			[Error(7, 18), Error(8, 18)],
			[Error(9, 18), Error(10, 18)],
			movement);

		introduced.ShouldBeEmpty();
		resolved.ShouldBe(0);
	}

	/// <summary>
	/// An error inside text the edit rewrote has no position to follow, so it falls back to matching on
	/// everything else rather than counting as one resolved and one introduced.
	/// </summary>
	[Test]
	public void Matches_an_error_inside_rewritten_text_on_everything_but_position()
	{
		var after = SourceText.From(Before.ToString().Replace(
			"\tvoid A() => Use(BottomBarHost);", "\tvoid A() => Use( BottomBarHost );", StringComparison.Ordinal));

		var (introduced, resolved) = DiagnosticDelta.Compare(
			[Error(7, 18)],
			[Error(7, 19)],
			TextMovement.Of(ShellPath, Before, after));

		introduced.ShouldBeEmpty();
		resolved.ShouldBe(0);
	}

	[Test]
	public void Counts_an_error_that_went_away_as_resolved()
	{
		var (introduced, resolved) = DiagnosticDelta.Compare(
			[Error(7, 18), Error(8, 18)],
			[Error(7, 18)],
			TextMovement.None);

		introduced.ShouldBeEmpty();
		resolved.ShouldBe(1);
	}

	[Test]
	public void Moves_a_position_below_an_insertion_and_leaves_one_above_it()
	{
		var movement = TextMovement.Of(ShellPath, Before, Insert(Before, line: 4, "\t\tvar a = 1;"));

		movement.Map(ShellPath, 3, 2).ShouldBe(((int, int)?)(3, 2));
		movement.Map(ShellPath, 8, 18).ShouldBe(((int, int)?)(9, 18));
	}

	/// <summary>
	/// Text rebuilt from a syntax root has no lineage back to the text it replaced, and Roslyn reports
	/// that as one change covering the whole file. The movement has to come out the same regardless.
	/// </summary>
	[Test]
	public void Follows_a_position_through_text_that_was_not_derived_from_the_original()
	{
		var rebuilt = SourceText.From(Insert(Before, line: 4, "\t\tvar a = 1;").ToString());

		TextMovement.Of(ShellPath, Before, rebuilt).Map(ShellPath, 8, 18).ShouldBe(((int, int)?)(9, 18));
	}

	[Test]
	public void Has_no_answer_for_a_line_the_edit_rewrote()
	{
		var rewritten = SourceText.From(Before.ToString().Replace("void A()", "void Renamed()", StringComparison.Ordinal));

		TextMovement.Of(ShellPath, Before, rewritten).Map(ShellPath, 7, 18).ShouldBeNull();
	}

	private static SourceText Insert(SourceText text, int line, string inserted)
	{
		var at = text.Lines[line - 1].EndIncludingLineBreak;

		return text.WithChanges(new TextChange(new TextSpan(at, 0), inserted + "\n"));
	}

	private static DiagnosticEntry Error(int line, int column) => new()
	{
		Id = "CS0229",
		Severity = "Error",
		Message = Ambiguous,
		Project = "KoboldDesktop.Android(net10.0-desktop)",
		FilePath = ShellPath,
		Line = line,
		Column = column,
	};
}
