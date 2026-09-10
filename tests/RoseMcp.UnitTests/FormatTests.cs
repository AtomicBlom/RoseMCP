using RoseMcp.Ui.Core;

namespace RoseMcp.UnitTests;

/// <summary>
/// The numbers and names as a RoseMCP window shows them.
/// <para>
/// Worth testing rather than eyeballing because these are the strings a reader acts on, and two of
/// them carry a distinction that is easy to lose in a refactor: an age of nothing is not an age of
/// zero, and a deadline that has passed is not a negative countdown.
/// </para>
/// </summary>
public sealed class FormatTests
{
	[Test]
	[Arguments(512L * 1024 * 1024, "512 MB")]
	[Arguments(2L * 1024 * 1024 * 1024, "2.0 GB")]
	public void Rounds_bytes_the_way_task_manager_does(long value, string expected) =>
		Assert.Equal(expected, Format.Bytes(value));

	/// <summary>
	/// Sub-second below a minute, because the interesting comparison for a warm call is against the
	/// tens of milliseconds it should have taken.
	/// </summary>
	[Test]
	public void Keeps_sub_second_precision_under_a_minute()
	{
		Assert.Equal("1.4s", Format.Duration(TimeSpan.FromSeconds(1.44)));
		Assert.Equal("2m 05s", Format.Duration(TimeSpan.FromSeconds(125)));
	}

	[Test]
	public void Reports_uptime_coarsely()
	{
		Assert.Equal("41s", Format.Uptime(TimeSpan.FromSeconds(41)));
		Assert.Equal("7m", Format.Uptime(TimeSpan.FromMinutes(7)));
		Assert.Equal("3h 12m", Format.Uptime(TimeSpan.FromMinutes(192)));
	}

	[Test]
	public void Says_a_tool_name_the_way_a_person_would()
	{
		Assert.Equal("find references", Format.Humanise("rose_find_references"));

		// A lifecycle label has no prefix and passes through unchanged.
		Assert.Equal("load solution", Format.Humanise("load solution"));
	}

	[Test]
	public void Agrees_with_itself_about_plurals()
	{
		Assert.Equal("1 solution", Format.Count(1, "solution"));
		Assert.Equal("2 solutions", Format.Count(2, "solution"));
		Assert.Equal("0 solutions", Format.Count(0, "solution"));
	}

	/// <summary>
	/// The load-bearing case: no age at all is a dash, not "0.0s ago". Nothing having happened yet
	/// and something happening this instant are opposite facts, and a heartbeat is exactly where
	/// conflating them would mislead -- a session whose host has never answered would read as one
	/// answering continuously.
	/// </summary>
	[Test]
	public void Says_a_missing_age_is_missing() => Assert.Equal("--", Format.Age(null));

	[Test]
	[Arguments(0.42, "0.4s ago")]
	[Arguments(9.9, "9.9s ago")]
	[Arguments(41.0, "41s ago")]
	[Arguments(200.0, "3m ago")]
	[Arguments(7200.0, "2h ago")]
	public void Coarsens_an_age_as_it_grows(double seconds, string expected) =>
		Assert.Equal(expected, Format.Age(TimeSpan.FromSeconds(seconds)));

	/// <summary>
	/// A deadline already past counts as now rather than counting upwards. A reader watching a hold
	/// expire wants to know it is over, and "-3s" invites the question of what a negative hold is.
	/// </summary>
	[Test]
	public void Never_counts_down_past_zero()
	{
		Assert.Equal("now", Format.Countdown(TimeSpan.Zero));
		Assert.Equal("now", Format.Countdown(TimeSpan.FromSeconds(-3)));
	}

	[Test]
	public void Counts_down_in_seconds_then_minutes()
	{
		Assert.Equal("47s", Format.Countdown(TimeSpan.FromSeconds(46.2)));
		Assert.Equal("1m 05s", Format.Countdown(TimeSpan.FromSeconds(65)));
	}

	[Test]
	public void Writes_an_il_offset_as_a_debugger_does()
	{
		Assert.Equal("IL_0042", Format.IlOffset(0x42));
		Assert.Equal("IL_0000", Format.IlOffset(0));

		// Wider when it has to be, rather than truncated.
		Assert.Equal("IL_12345", Format.IlOffset(0x12345));
	}

	/// <summary>
	/// The file name only, because a card is one line wide. A path with no line is still worth
	/// showing; a missing path is not, and comes back empty so a caller can hide the whole element.
	/// </summary>
	[Test]
	public void Reduces_a_source_position_to_a_file_and_a_line()
	{
		Assert.Equal("Widget.xaml.cs:42", Format.FileLine(@"D:\app\src\Widget.xaml.cs", 42));
		Assert.Equal("Widget.xaml.cs", Format.FileLine(@"D:\app\src\Widget.xaml.cs", null));
		Assert.Equal(string.Empty, Format.FileLine(null, 42));
		Assert.Equal(string.Empty, Format.FileLine(string.Empty, 42));
	}

	[Test]
	public void Says_when_there_is_no_process()
	{
		Assert.Equal("pid 1234", Format.Pid(1234));
		Assert.Equal("no process", Format.Pid(null));
	}
}
