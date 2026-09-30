using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// Turning a method's source and its positions into the rows somebody clicks to place a breakpoint.
/// <para>
/// The assertion that carries the feature is the one about a line with two methods on it. A row that
/// quietly picked one of them would set a breakpoint in code the reader never pointed at, and the
/// only symptom would be the target stopping somewhere they did not expect.
/// </para>
/// </summary>
public sealed class SourceViewTests
{
	[Test]
	public void A_line_nothing_stops_on_is_text_and_not_a_place_to_break()
	{
		var view = SourceView.Build(Method(["{", "// a comment", "}"], firstLine: 10));

		view.Count.ShouldBe(3);
		foreach (var row in view)
		{
			row.IsBreakable.ShouldBeFalse();
		}
		view.Select(row => row.Line).ShouldBe([10, 11, 12]);
		view[1].Text.ShouldBe("// a comment");
	}

	/// <summary>
	/// A statement compiles to several instructions and every one of them is a position, but they are
	/// the same line of the same method: a reader has nothing to choose between them, so they are one
	/// row addressing where the statement starts.
	/// </summary>
	[Test]
	public void Positions_in_one_method_on_one_line_are_a_single_row()
	{
		var view = SourceView.Build(Method(
			["var total = first + second;"],
			firstLine: 4,
			At(4, "Widget.Refresh", "App!Widget.Refresh@IL_0014", 0x14),
			At(4, "Widget.Refresh", "App!Widget.Refresh@IL_0009", 9)));

		var row = view.ShouldHaveSingleItem();
		row.IsBreakable.ShouldBeTrue();
		row.Location.ShouldBe("App!Widget.Refresh@IL_0009");
		row.OffsetLabel.ShouldBe("IL_0009");
		row.IsContinuation.ShouldBeFalse();
	}

	/// <summary>
	/// The case the whole picker exists for. A lambda's body is a method of its own, so a line
	/// carrying one has two places to stop, in two methods -- and which one somebody meant is not
	/// something the line can say.
	/// </summary>
	[Test]
	public void A_line_with_a_lambda_on_it_offers_both_methods()
	{
		var source = Method(
			["var names = items.Select(item => item.Name);"],
			firstLine: 7,
			At(7, "Widget.Refresh", "App!Widget.Refresh@IL_0011", 0x11),
			At(7, "Widget.Refresh (lambda)", "App!Widget+<>c.<Refresh>b__3_0@IL_0000", 0));

		var view = SourceView.Build(source);

		view.Count.ShouldBe(2);

		// The code is written once, on the row carrying the line number, and is not named: it is the
		// method in front of the reader, and saying so on every line is noise.
		view[0].Text.ShouldBe("var names = items.Select(item => item.Name);");
		view[0].LineLabel.ShouldBe("7");
		view[0].Location.ShouldBe("App!Widget.Refresh@IL_0011");
		view[0].HasNote.ShouldBeFalse();

		// And the second place to stop is a row of its own, named, because it is somewhere else.
		view[1].IsContinuation.ShouldBeTrue();
		view[1].Text.ShouldBeEmpty();
		view[1].LineLabel.ShouldBeEmpty();
		view[1].Line.ShouldBe(7);
		view[1].Location.ShouldBe("App!Widget+<>c.<Refresh>b__3_0@IL_0000");
		view[1].Note.ShouldBe("Widget.Refresh (lambda)");
		view[1].HasNote.ShouldBeTrue();
	}

	/// <summary>
	/// Line numbers are padded to the widest in view, so the code starts at one column rather than
	/// stepping right as the numbers gain a digit.
	/// </summary>
	[Test]
	public void Line_numbers_line_up()
	{
		var view = SourceView.Build(Method(["a", "b", "c"], firstLine: 99));

		view.Select(row => row.LineLabel).ShouldBe([" 99", "100", "101"]);
	}

	/// <summary>
	/// A method whose source is not on this machine has no rows at all. The pane says why and offers
	/// the method's first instruction instead; an empty listing of lines would read as a method with
	/// no code in it.
	/// </summary>
	[Test]
	public void A_method_with_no_text_builds_no_rows()
	{
		SourceView.Build(Method([], firstLine: 0)).ShouldBeEmpty();
	}

	private static LiveMethodPosition At(int line, string owner, string location, int ilOffset) => new()
	{
		Location = location,
		DisplayName = owner,
		IlOffset = ilOffset,
		Line = line,
		Column = 3,
		EndLine = line,
		EndColumn = 20,
	};

	private static LiveMethodSource Method(string[] lines, int firstLine, params LiveMethodPosition[] positions) => new()
	{
		Location = "App!Widget.Refresh",
		DisplayName = "Widget.Refresh",
		Module = "App",
		Symbols = LiveSymbolState.Resolved,
		File = @"C:\build\Widget.cs",
		FirstLine = firstLine,
		Lines = lines,
		Positions = positions,
	};
}
