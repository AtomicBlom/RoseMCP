using System.Text.RegularExpressions;

using ModelContextProtocol.Protocol;

namespace RoseMcp.Broker;

/// <summary>
/// What a failure in another process actually said, read back off the wire.
/// <para>
/// The SDK formats every tool failure as <c>An error occurred invoking '{tool}': {message}</c>, and
/// it does that once per hop. A worker's own explanation therefore arrives already wearing one
/// preamble, and the broker's boundary filter -- which exists so the message survives at all -- put
/// a second one in front of it before handing it to the client. The caller read the tool name twice
/// and the useful sentence third.
/// </para>
/// <para>
/// So the inner preamble is taken off here rather than the outer one suppressed at the boundary:
/// the outer one is the SDK's own doing and is what a client expects, and the inner one is an
/// artefact of there being a process in the middle.
/// </para>
/// </summary>
public static partial class ForwardedError
{
	/// <summary>The message a failed call reported, or null where it did not fail.</summary>
	/// <param name="result">What the far side returned.</param>
	public static string? Message(CallToolResult result)
	{
		if (result.IsError != true) return null;

		var text = string.Join(
			Environment.NewLine,
			result.Content.OfType<TextContentBlock>().Select(block => block.Text));

		return string.IsNullOrWhiteSpace(text) ? null : Preamble().Replace(text, string.Empty, 1);
	}

	/// <summary>
	/// The SDK's own wrapper, anchored at the start so a message that merely quotes the sentence
	/// keeps it.
	/// </summary>
	[GeneratedRegex(@"^An error occurred invoking '[^']*': ")]
	private static partial Regex Preamble();
}
