using System.Text.Json;

using ModelContextProtocol.Protocol;

using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// The relay re-issued anything after a transport failure, which the broker behind it refuses to do
/// for exactly the tools that matter. A broken socket to a living tray is a transport failure, and
/// then the rename was applied and the retry applies it again -- a position-addressed one, re-run
/// after the identifier changed length, lands on a different token.
/// </summary>
public sealed class RelayRetryPolicyTests
{
	[Test]
	public void Retries_nothing_until_the_tool_list_has_arrived()
	{
		var policy = new RelayRetryPolicy();

		policy.Known.ShouldBeFalse("nothing is known before the tool list arrives");
		policy.MayRetry("rose_outline").ShouldBeFalse("a read-only tool is not retried before the list arrives");
		policy.MayRetry("rose_rename_symbol").ShouldBeFalse("a writing tool is not retried before the list arrives");
	}

	[Test]
	public void Retries_a_read_only_tool_and_not_a_writing_one()
	{
		var policy = new RelayRetryPolicy();

		policy.Learn([ReadOnly("rose_outline"), Writes("rose_rename_symbol")]);

		policy.Known.ShouldBeTrue();
		policy.MayRetry("rose_outline").ShouldBeTrue();
		policy.MayRetry("rose_rename_symbol").ShouldBeFalse("a writing tool is never retried");
	}

	/// <summary>
	/// A tool the list did not mention, and one whose annotation says nothing. Both are unknown, and
	/// unknown is no: one clear failure the caller can repeat is cheaper than a second edit nobody
	/// asked for.
	/// </summary>
	[Test]
	public void Refuses_a_tool_it_was_told_nothing_about()
	{
		var policy = new RelayRetryPolicy();

		policy.Learn([ReadOnly("rose_outline"), Named("rose_mystery")]);

		policy.MayRetry("rose_mystery").ShouldBeFalse("a tool it was told nothing about is not retried");
		policy.MayRetry("rose_never_heard_of_it").ShouldBeFalse("nor is any other tool it was told nothing about");
	}

	/// <summary>
	/// The tray's surface is what it says it is now, not the union of everything it has ever said: a
	/// tool that stops being read-only across a tray upgrade must stop being retried.
	/// </summary>
	[Test]
	public void Replaces_what_it_knew_rather_than_merging()
	{
		var policy = new RelayRetryPolicy();

		policy.Learn([ReadOnly("rose_format")]);
		policy.MayRetry("rose_format").ShouldBeTrue();

		policy.Learn([Writes("rose_format")]);
		policy.MayRetry("rose_format").ShouldBeFalse("a tool from the list it replaced is no longer retried");
	}

	private static Tool ReadOnly(string name) => Named(name, new ToolAnnotations { ReadOnlyHint = true });

	private static Tool Writes(string name) => Named(name, new ToolAnnotations { ReadOnlyHint = false });

	/// <summary>
	/// A tool as the tray's list would carry it. The schema is the smallest one the SDK accepts, since
	/// Tool validates what it is handed and none of it is what this is about.
	/// </summary>
	private static Tool Named(string name, ToolAnnotations? annotations = null) => new()
	{
		Name = name,
		InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
		Annotations = annotations,
	};
}
