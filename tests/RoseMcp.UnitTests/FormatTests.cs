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
		Format.Bytes(value).ShouldBe(expected);

	/// <summary>
	/// Sub-second below a minute, because the interesting comparison for a warm call is against the
	/// tens of milliseconds it should have taken.
	/// </summary>
	[Test]
	public void Keeps_sub_second_precision_under_a_minute()
	{
		Format.Duration(TimeSpan.FromSeconds(1.44)).ShouldBe("1.4s");
		Format.Duration(TimeSpan.FromSeconds(125)).ShouldBe("2m 05s");
	}

	[Test]
	public void Reports_uptime_coarsely()
	{
		Format.Uptime(TimeSpan.FromSeconds(41)).ShouldBe("41s");
		Format.Uptime(TimeSpan.FromMinutes(7)).ShouldBe("7m");
		Format.Uptime(TimeSpan.FromMinutes(192)).ShouldBe("3h 12m");
	}

	[Test]
	public void Says_a_tool_name_the_way_a_person_would()
	{
		Format.Humanise("rose_find_references").ShouldBe("find references");

		// A lifecycle label has no prefix and passes through unchanged.
		Format.Humanise("load solution").ShouldBe("load solution");
	}

	[Test]
	public void Agrees_with_itself_about_plurals()
	{
		Format.Count(1, "solution").ShouldBe("1 solution");
		Format.Count(2, "solution").ShouldBe("2 solutions");
		Format.Count(0, "solution").ShouldBe("0 solutions");
	}

	/// <summary>
	/// The load-bearing case: no age at all is a dash, not "0.0s ago". Nothing having happened yet
	/// and something happening this instant are opposite facts, and a heartbeat is exactly where
	/// conflating them would mislead -- a session whose host has never answered would read as one
	/// answering continuously.
	/// </summary>
	[Test]
	public void Says_a_missing_age_is_missing() => Format.Age(null).ShouldBe("--");

	[Test]
	[Arguments(0.42, "0.4s ago")]
	[Arguments(9.9, "9.9s ago")]
	[Arguments(41.0, "41s ago")]
	[Arguments(200.0, "3m ago")]
	[Arguments(7200.0, "2h ago")]
	public void Coarsens_an_age_as_it_grows(double seconds, string expected) =>
		Format.Age(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

	/// <summary>
	/// A deadline already past counts as now rather than counting upwards. A reader watching a hold
	/// expire wants to know it is over, and "-3s" invites the question of what a negative hold is.
	/// </summary>
	[Test]
	public void Never_counts_down_past_zero()
	{
		Format.Countdown(TimeSpan.Zero).ShouldBe("now");
		Format.Countdown(TimeSpan.FromSeconds(-3)).ShouldBe("now");
	}

	[Test]
	public void Counts_down_in_seconds_then_minutes()
	{
		Format.Countdown(TimeSpan.FromSeconds(46.2)).ShouldBe("47s");
		Format.Countdown(TimeSpan.FromSeconds(65)).ShouldBe("1m 05s");
	}

	[Test]
	public void Writes_an_il_offset_as_a_debugger_does()
	{
		Format.IlOffset(0x42).ShouldBe("IL_0042");
		Format.IlOffset(0).ShouldBe("IL_0000");

		// Wider when it has to be, rather than truncated.
		Format.IlOffset(0x12345).ShouldBe("IL_12345");
	}

	/// <summary>
	/// The file name only, because a card is one line wide. A path with no line is still worth
	/// showing; a missing path is not, and comes back empty so a caller can hide the whole element.
	/// </summary>
	[Test]
	public void Reduces_a_source_position_to_a_file_and_a_line()
	{
		Format.FileLine(@"D:\app\src\Widget.xaml.cs", 42).ShouldBe("Widget.xaml.cs:42");
		Format.FileLine(@"D:\app\src\Widget.xaml.cs", null).ShouldBe("Widget.xaml.cs");
		Format.FileLine(null, 42).ShouldBe(string.Empty);
		Format.FileLine(string.Empty, 42).ShouldBe(string.Empty);
	}

	/// <summary>
	/// Either separator, on either operating system. The path is a record of where something was
	/// compiled rather than a path on the machine reading it, so a Windows path routinely arrives on a
	/// Linux one -- and there a backslash is an ordinary character in a file name, so asking the
	/// framework for the file name hands back the whole path and the caption becomes unreadable.
	/// </summary>
	[Test]
	public void Takes_the_file_name_off_a_path_written_by_another_operating_system()
	{
		Format.FileLine(@"D:\repo\src\Widget.cs", 42).ShouldBe("Widget.cs:42");
		Format.FileLine("/home/build/repo/src/Widget.cs", 42).ShouldBe("Widget.cs:42");
		Format.FileLine("Widget.cs", 42).ShouldBe("Widget.cs:42");
	}

	[Test]
	public void Says_when_there_is_no_process()
	{
		Format.Pid(1234).ShouldBe("pid 1234");
		Format.Pid(null).ShouldBe("no process");
	}
}
