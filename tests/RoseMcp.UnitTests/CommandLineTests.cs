using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// Reading a raw command line back into arguments, which is what a redirected activation hands the
/// instance already running.
/// <para>
/// Every case here is a path with a space in it or a quote next to a separator, because that is the
/// whole reason this is not a split on whitespace. Getting it wrong sends the inspector to a
/// session id that is half a directory name.
/// </para>
/// </summary>
public sealed class CommandLineTests
{
	[Test]
	public void The_executable_is_dropped_and_the_rest_kept()
	{
		CommandLine.Split(@"C:\rose\inspector\RoseMcp.Inspector.exe --port 5077 --session a1b2c3d4").ShouldBe(
			["--port", "5077", "--session", "a1b2c3d4"]);
	}

	/// <summary>A path with a space in it is one argument, which is the case this exists for.</summary>
	[Test]
	public void A_quoted_argument_with_spaces_stays_one_argument()
	{
		CommandLine.Split(@"""C:\Program Files\Rose\RoseMcp.Inspector.exe"" --token ""a b c""").ShouldBe(
			["--token", "a b c"]);
	}

	/// <summary>
	/// A run of backslashes before a quote is halved, and an odd one escapes the quote. It is the
	/// rule the C runtime uses, and the reason a quoted path ending in a separator survives.
	/// </summary>
	[Test]
	public void Backslashes_before_a_quote_follow_the_runtime_rule()
	{
		// Two backslashes before the closing quote halve to one, and the quote still closes -- which
		// is what lets a quoted path end in a separator.
		CommandLine.Split(@"exe ""C:\dir\\""").ShouldBe([@"C:\dir\"]);

		// An odd backslash escapes the quote rather than ending the quoted run.
		CommandLine.Split(@"exe say\""it").ShouldBe([@"say""it"]);

		// Backslashes that are not before a quote are literal, however many there are.
		CommandLine.Split(@"exe ""C:\dir\\ next""").ShouldBe([@"C:\dir\\ next"]);
	}

	[Test]
	public void Runs_of_whitespace_do_not_make_empty_arguments()
	{
		CommandLine.Split("exe   --port \t 5077  ").ShouldBe(["--port", "5077"]);
	}

	[Test]
	public void An_empty_quoted_argument_is_still_an_argument()
	{
		CommandLine.Split(@"exe --token """"").ShouldBe(["--token", string.Empty]);
	}

	[Test]
	public void Nothing_at_all_reads_as_no_arguments()
	{
		CommandLine.Split(null).ShouldBeEmpty();
		CommandLine.Split(string.Empty).ShouldBeEmpty();
		CommandLine.Split("exe").ShouldBeEmpty();
	}

	/// <summary>
	/// Keeping the first token is for a caller that has arguments without an executable in front of
	/// them, which is what a process start gives.
	/// </summary>
	[Test]
	public void The_first_token_can_be_kept()
	{
		CommandLine.Split("--port 5077", skipExecutable: false).ShouldBe(["--port", "5077"]);
	}

	/// <summary>What a command line composes to, read back, is what went into it.</summary>
	[Test]
	public void What_the_tray_composes_parses_back()
	{
		var line = @"""C:\Program Files\Rose\inspector\RoseMcp.Inspector.exe"" --port 5077 --token tok-1 --session s1";

		InspectorOptions.Parse(CommandLine.Split(line)).ShouldBe(
			new InspectorOptions { Port = 5077, Token = "tok-1", SessionId = "s1" });
	}
}
