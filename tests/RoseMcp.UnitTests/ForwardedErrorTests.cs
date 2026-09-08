using ModelContextProtocol.Protocol;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// Reading a failure back off the wire. Every hop the SDK carries a tool failure over wraps the
/// message in "An error occurred invoking '{tool}': ", and this process is in the middle of two --
/// so a worker's own sentence reached the caller with the tool name in front of it twice and the
/// useful part third.
/// </summary>
public sealed class ForwardedErrorTests
{
	[Test]
	public void Takes_off_the_wrapper_the_far_side_added()
	{
		var message = ForwardedError.Message(Failed(
			"An error occurred invoking 'rose_replace_member': The code declares 2 members and this "
				+ "replaces one. (workspace: A.slnx)"));

		Assert.Equal("The code declares 2 members and this replaces one. (workspace: A.slnx)", message);
	}

	/// <summary>Once, so a message that quotes the sentence itself keeps its own copy.</summary>
	[Test]
	public void Takes_it_off_once()
	{
		var message = ForwardedError.Message(Failed(
			"An error occurred invoking 'rose_format': An error occurred invoking 'rose_format': no"));

		Assert.Equal("An error occurred invoking 'rose_format': no", message);
	}

	[Test]
	public void Leaves_a_message_that_never_had_one()
	{
		Assert.Equal("Nothing in the solution is called 'Widget'.", ForwardedError.Message(Failed(
			"Nothing in the solution is called 'Widget'.")));
	}

	/// <summary>A result that did not fail has no message, which is how the caller tells.</summary>
	[Test]
	public void Reports_nothing_for_a_result_that_succeeded()
	{
		Assert.Null(ForwardedError.Message(new CallToolResult { Content = [] }));
		Assert.Null(ForwardedError.Message(new CallToolResult { IsError = true, Content = [] }));
	}

	private static CallToolResult Failed(string text) =>
		new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
