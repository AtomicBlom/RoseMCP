namespace RoseMcp.UnitTests;

/// <summary>
/// Counting a change no diff can show. It is a small function guarding a specific hole: a unified
/// diff compares line content, a terminator is not content, and rewriting terminators is the
/// commonest thing formatting does in a repository that has an opinion about them.
/// </summary>
public sealed class LineEndingsTests
{
	[Fact]
	public void Counts_every_line_that_changed_terminator()
	{
		var changed = LineEndings.Changed("one\ntwo\nthree\n", "one\r\ntwo\r\nthree\r\n");

		Assert.NotNull(changed);
		Assert.Equal(3, changed.Value.Lines);
		Assert.Equal("CRLF", changed.Value.To);
	}

	/// <summary>The whole point of the return being nullable: no change is not a change of zero.</summary>
	[Theory]
	[InlineData("one\r\ntwo\r\n", "one\r\ntwo\r\n")]
	[InlineData("one\ntwo\n", "one\ntwo\n")]
	[InlineData("", "")]
	public void Says_nothing_where_the_terminators_are_the_same(string before, string after) =>
		Assert.Null(LineEndings.Changed(before, after));

	/// <summary>
	/// Content changing on its own is the case the diff already covers, and reporting it here would
	/// put a line-endings notice on every ordinary edit.
	/// </summary>
	[Fact]
	public void Says_nothing_where_only_the_content_changed()
	{
		Assert.Null(LineEndings.Changed("one\r\ntwo\r\n", "one\r\nTWO\r\n"));
	}

	/// <summary>A file part-converted already reports only the lines that actually moved.</summary>
	[Fact]
	public void Counts_only_the_lines_that_moved()
	{
		var changed = LineEndings.Changed("one\r\ntwo\nthree\n", "one\r\ntwo\r\nthree\r\n");

		Assert.NotNull(changed);
		Assert.Equal(2, changed.Value.Lines);
		Assert.Equal("CRLF", changed.Value.To);
	}

	/// <summary>Both directions, because a repository that wants LF is as entitled to be told.</summary>
	[Fact]
	public void Reports_the_direction_it_actually_went()
	{
		var changed = LineEndings.Changed("one\r\ntwo\r\n", "one\ntwo\n");

		Assert.NotNull(changed);
		Assert.Equal("LF", changed.Value.To);
	}

	/// <summary>
	/// The majority, not the last one seen. A file left with two endings is not something a
	/// formatter produces, but naming whichever happened to come last would be wrong on the day it
	/// does.
	/// </summary>
	[Fact]
	public void Names_the_terminator_most_of_the_changed_lines_took()
	{
		var changed = LineEndings.Changed("a\nb\nc\n", "a\r\nb\r\nc\n");

		Assert.NotNull(changed);
		Assert.Equal(2, changed.Value.Lines);
		Assert.Equal("CRLF", changed.Value.To);
	}

	/// <summary>
	/// Lines added or removed put the two files out of step, and past that point a positional
	/// comparison means nothing -- but it also does not need to, because a change of that shape is
	/// one the diff shows in full.
	/// </summary>
	[Fact]
	public void Does_not_invent_a_change_when_lines_were_added()
	{
		Assert.Null(LineEndings.Changed("one\r\n", "one\r\ntwo\r\nthree\r\n"));
	}

	[Theory]
	[InlineData("\r\n", "CRLF")]
	[InlineData("\n", "LF")]
	[InlineData("\r", "CR")]
	public void Names_a_terminator_the_way_a_person_would(string ending, string expected) =>
		Assert.Equal(expected, LineEndings.Name(ending));
}
