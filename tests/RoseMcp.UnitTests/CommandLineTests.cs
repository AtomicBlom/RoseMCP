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
		Assert.Equal(
			["--port", "5077", "--session", "a1b2c3d4"],
			CommandLine.Split(@"C:\rose\inspector\RoseMcp.Inspector.exe --port 5077 --session a1b2c3d4"));
	}

	/// <summary>A path with a space in it is one argument, which is the case this exists for.</summary>
	[Test]
	public void A_quoted_argument_with_spaces_stays_one_argument()
	{
		Assert.Equal(
			["--token", "a b c"],
			CommandLine.Split(@"""C:\Program Files\Rose\RoseMcp.Inspector.exe"" --token ""a b c"""));
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
		Assert.Equal([@"C:\dir\"], CommandLine.Split(@"exe ""C:\dir\\"""));

		// An odd backslash escapes the quote rather than ending the quoted run.
		Assert.Equal([@"say""it"], CommandLine.Split(@"exe say\""it"));

		// Backslashes that are not before a quote are literal, however many there are.
		Assert.Equal([@"C:\dir\\ next"], CommandLine.Split(@"exe ""C:\dir\\ next"""));
	}

	[Test]
	public void Runs_of_whitespace_do_not_make_empty_arguments()
	{
		Assert.Equal(["--port", "5077"], CommandLine.Split("exe   --port \t 5077  "));
	}

	[Test]
	public void An_empty_quoted_argument_is_still_an_argument()
	{
		Assert.Equal(["--token", string.Empty], CommandLine.Split(@"exe --token """""));
	}

	[Test]
	public void Nothing_at_all_reads_as_no_arguments()
	{
		Assert.Empty(CommandLine.Split(null));
		Assert.Empty(CommandLine.Split(string.Empty));
		Assert.Empty(CommandLine.Split("exe"));
	}

	/// <summary>
	/// Keeping the first token is for a caller that has arguments without an executable in front of
	/// them, which is what a process start gives.
	/// </summary>
	[Test]
	public void The_first_token_can_be_kept()
	{
		Assert.Equal(["--port", "5077"], CommandLine.Split("--port 5077", skipExecutable: false));
	}

	/// <summary>What a command line composes to, read back, is what went into it.</summary>
	[Test]
	public void What_the_tray_composes_parses_back()
	{
		var line = @"""C:\Program Files\Rose\inspector\RoseMcp.Inspector.exe"" --port 5077 --token tok-1 --session s1";

		Assert.Equal(
			new InspectorOptions { Port = 5077, Token = "tok-1", SessionId = "s1" },
			InspectorOptions.Parse(CommandLine.Split(line)));
	}
}
