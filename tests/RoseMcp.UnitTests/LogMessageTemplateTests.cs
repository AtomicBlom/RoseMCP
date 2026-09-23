using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// A tracepoint's log message and the values it interpolates. Both of its failures are quiet: a
/// placeholder mistaken for literal text logs the same wrong line on every hit and reads as a
/// debugger that ignored the message, and a literal brace mistaken for a placeholder turns a fixed
/// message into an error about a name nobody wrote. Neither shows up as a failure anywhere, and
/// neither can be reached by a test without a running debuggee, which is why the grammar is pinned
/// here.
/// </summary>
public sealed class LogMessageTemplateTests
{
	/// <summary>Resolves every placeholder to its own path text, so the joining is what is under test.</summary>
	private static string Render(string message, Func<ValuePath, LogValue>? resolve = null)
		=> LogMessageTemplate.Parse(message)!.Render(resolve ?? (path => LogValue.Read($"<{path.Name}>", "int"))).Text;

	[Test]
	public void An_absent_message_is_not_a_template()
	{
		Assert.Null(LogMessageTemplate.Parse(null));
		Assert.Null(LogMessageTemplate.Parse(string.Empty));
	}

	/// <summary>
	/// A message of literal text says so, because rendering one means reading the frame it was hit
	/// on, and a tracepoint on a hot method pays that on every hit for nothing.
	/// </summary>
	[Test]
	public void Literal_text_interpolates_nothing()
	{
		var template = LogMessageTemplate.Parse("reached the refresh")!;

		Assert.False(template.Interpolates);
		Assert.Equal("reached the refresh", template.Render(_ => LogValue.Unavailable("never asked")).Text);
	}

	[Test]
	[Arguments("count={count}", "count=<count>")]
	[Arguments("{count}", "<count>")]
	[Arguments("{a} then {b}", "<a> then <b>")]
	[Arguments("{ count }", "<count>")]
	public void A_placeholder_is_replaced_by_its_value(string message, string expected)
		=> Assert.Equal(expected, Render(message));

	/// <summary>
	/// A doubled brace is one literal brace, which is the only way to log text that contains one --
	/// and an object renders as <c>{TypeName}</c>, so braces in a message are not hypothetical.
	/// </summary>
	[Test]
	[Arguments("{{count}}", "{count}")]
	[Arguments("{{", "{")]
	[Arguments("}}", "}")]
	[Arguments("{{{count}}}", "{<count>}")]
	public void Doubled_braces_are_literal(string message, string expected)
	{
		Assert.False(LogMessageTemplate.Parse("{{}}")!.Interpolates);
		Assert.Equal(expected, Render(message));
	}

	/// <summary>
	/// An unbalanced brace is refused at the call that wrote it. A tracepoint that accepted it would
	/// log the placeholder verbatim on every hit, which reads as interpolation not being supported
	/// rather than as a typo in the one message that has it.
	/// </summary>
	[Test]
	[Arguments("count={count")]
	[Arguments("count=}")]
	[Arguments("{}")]
	[Arguments("{ }")]
	[Arguments("{state..Count}")]
	[Arguments("{items[}")]
	public void A_malformed_message_is_refused(string message)
		=> Assert.Throws<ArgumentException>(() => LogMessageTemplate.Parse(message));

	/// <summary>
	/// A placeholder is a value path, the same grammar an evaluation takes, so what a caller was
	/// shown in a variable can be pasted into a message without translation.
	/// </summary>
	[Test]
	public void A_placeholder_is_a_value_path()
	{
		var template = LogMessageTemplate.Parse("{state.Inner.Count} {arg:0} {items[3].Name}")!;
		var paths = template.Segments.Where(segment => segment.Path is not null).Select(segment => segment.Path!).ToList();

		Assert.Equal(3, paths.Count);
		Assert.Equal(ValuePathRoot.Name, paths[0].Kind);
		Assert.Equal(2, paths[0].Steps.Count);
		Assert.Equal(ValuePathRoot.Argument, paths[1].Kind);
		Assert.Equal(0, paths[1].Slot);
		Assert.Equal(3, paths[2].Steps[0].Index);
	}

	/// <summary>
	/// A value that could not be read keeps its place, with the reason. A local goes in and out of
	/// scope within one method, so this is ordinary rather than exceptional -- and a gap where a
	/// value should be reads as the value having been empty, which is the one answer worse than none.
	/// </summary>
	[Test]
	public void An_unresolved_value_says_so_where_it_would_have_been()
	{
		var rendered = LogMessageTemplate.Parse("count={count} name={name}")!
			.Render(path => path.Name == "count"
				? LogValue.Read("7", "int")
				: LogValue.Unavailable("not an argument or local in this frame"));

		Assert.Equal("count=7 name=<name: not an argument or local in this frame>", rendered.Text);
	}

	/// <summary>
	/// The values come out as data as well as in the line. A page of hits is long enough to be
	/// truncated by whatever is displaying it, and the way back from that is to ask for one event
	/// and read its fields -- which only works if the fields carry the values rather than a sentence
	/// containing them.
	/// </summary>
	[Test]
	public void The_values_come_out_as_data_in_the_order_they_appear()
	{
		var rendered = LogMessageTemplate.Parse("{count} items for {name}, again")!
			.Render(path => path.Name == "count"
				? LogValue.Read("7", "int")
				: LogValue.Unavailable("out of scope"));

		Assert.Equal(2, rendered.Values.Count);

		Assert.Equal("count", rendered.Values[0].Name);
		Assert.Equal("count", rendered.Values[0].Path);
		Assert.Equal(LogMessageTemplate.LoggedKind, rendered.Values[0].Kind);
		Assert.Equal("int", rendered.Values[0].TypeName);
		Assert.Equal("7", rendered.Values[0].Value);

		// A value that was not there is still reported, because which one went missing is a fact
		// about the hit and dropping it leaves a reader counting positions to work out which.
		Assert.Equal("name", rendered.Values[1].Name);
		Assert.Null(rendered.Values[1].TypeName);
		Assert.Equal("<name: out of scope>", rendered.Values[1].Value);
	}

	/// <summary>A value the reader could not render at all is unreadable, not empty.</summary>
	[Test]
	public void A_value_that_rendered_to_nothing_reports_as_unreadable()
		=> Assert.Equal("(unreadable)", Render("{count}", _ => LogValue.Read(null, "int")));
}
