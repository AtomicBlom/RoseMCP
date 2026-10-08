using System.Text.Json;
using ModelContextProtocol.Server;
using RoseMcp.Broker.Tools;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// What a caller is told when an argument is the wrong shape. The binder's own account names a CLR
/// type nobody wrote and points at the root of the document, which is accurate and unusable: a tool
/// with three string arguments and one array does not say which of the four was sent wrong.
/// </summary>
public sealed class ToolArgumentShapeTests
{
	/// <summary>
	/// The reported case: a list of strings sent as a bare string. The argument is named, the wanted
	/// shape is spelled in the caller's own vocabulary rather than as System.String[], and the
	/// example carries the brackets, which are the whole correction.
	/// </summary>
	[Test]
	public void Names_the_argument_that_wanted_a_list()
	{
		var message = ToolArgumentShape.Mismatch(
			Schema("""{"arguments":{"type":["array","null"],"items":{"type":"string"}},"symbol":{"type":"string"}}"""),
			Arguments("""{"symbol":"A.B","arguments":"count=1"}"""));

		message.ShouldNotBeNull();
		message.ShouldStartWith("arguments takes a list of strings", Case.Sensitive);
		message!.ShouldContain("a string was sent", Case.Sensitive);
		message.ShouldContain("[\"one\", \"two\"]", Case.Sensitive);
		message.ShouldNotContain("System.String", Case.Sensitive);
	}

	/// <summary>
	/// A boolean sent as the string an agent writes when it is composing JSON by hand. The same
	/// sentence shape, because a caller who learns it once should not have to learn it again per
	/// type.
	/// </summary>
	[Test]
	public void Names_the_argument_that_wanted_a_boolean()
	{
		var message = ToolArgumentShape.Mismatch(
			Schema("""{"apply":{"type":"boolean"}}"""),
			Arguments("""{"apply":"yes"}"""));

		message.ShouldNotBeNull();
		message.ShouldStartWith("apply takes a boolean", Case.Sensitive);
		message!.ShouldContain("true or false", Case.Sensitive);
	}

	/// <summary>
	/// A nullable argument is declared as a union with null in it, and null is always allowed -- so
	/// sending one is not the mismatch, whatever else the call was refused for.
	/// </summary>
	[Test]
	public void Says_nothing_about_a_null_sent_for_a_nullable_argument()
	{
		ToolArgumentShape.Mismatch(
			Schema("""{"workspace":{"type":["string","null"]}}"""),
			Arguments("""{"workspace":null}""")).ShouldBeNull();
	}

	/// <summary>
	/// Nothing is said where every argument matches its declared type. This runs only after the
	/// binder has already refused a call, and a refusal it cannot explain has to fall back to the
	/// binder's own words rather than name an argument that is fine.
	/// </summary>
	[Test]
	public void Says_nothing_when_every_argument_matches()
	{
		ToolArgumentShape.Mismatch(
			Schema("""{"symbol":{"type":"string"},"usings":{"type":["array","null"],"items":{"type":"string"}}}"""),
			Arguments("""{"symbol":"A.B","usings":["System.Text"]}""")).ShouldBeNull();
	}

	/// <summary>
	/// An argument the schema does not declare, and a schema with no properties at all: both are
	/// shapes this cannot judge, and judging them anyway would refuse a call for the wrong reason.
	/// </summary>
	[Test]
	public void Says_nothing_about_what_the_schema_does_not_declare()
	{
		ToolArgumentShape.Mismatch(
			Schema("""{"symbol":{"type":"string"}}"""),
			Arguments("""{"unknown":42}""")).ShouldBeNull();

		ToolArgumentShape.Mismatch(
			JsonDocument.Parse("""{"type":"object"}""").RootElement,
			Arguments("""{"symbol":42}""")).ShouldBeNull();
	}

	/// <summary>
	/// <c>file</c> for <c>filePath</c>, read against the schema alone, as a boundary with no alias
	/// filter in front of it reads it: the binder drops the argument and the tool reports the absence
	/// of a value the caller supplied. Four edits away, far more than a typo, and still plainly what
	/// was meant, because it is part of the name.
	/// </summary>
	[Test]
	public void A_refusal_names_the_argument_it_never_saw_and_the_one_it_meant()
	{
		var message = ToolArgumentShape.Refusal(
			"Name a type, as `Namespace.Type`, or give a file path.",
			binderRefused: false,
			"rose_outline",
			Outline,
			Arguments("""{"file":"src/A.cs"}"""));

		message.ShouldBe(
			"Name a type, as `Namespace.Type`, or give a file path. "
				+ "`file` is not an argument of `rose_outline`. Did you mean `filePath`?");
	}

	/// <summary>
	/// The expensive half: a call that ran without its argument. The notice has the same suggestion
	/// the refusal would, because it comes from the same reading of the same schema.
	/// </summary>
	[Test]
	public void A_call_that_ran_is_told_what_it_ignored()
	{
		var notices = ToolArgumentShape.Ignored(
			"rose_find_references",
			Schema("""{"symbol":{"type":["string","null"]},"filePath":{"type":["string","null"]},"project":{"type":["string","null"]}}"""),
			Arguments("""{"symbol":"X","projet":"A"}"""));

		notices.ShouldBe([
			"Ignored an argument called `projet`; `rose_find_references` has no such argument. Did you mean `project`?",
		]);
	}

	/// <summary>
	/// Nearest wins where two declared names are close, so <c>path</c> means the file rather than the
	/// project file; equally near names are all offered rather than one picked between them.
	/// </summary>
	[Test]
	public void The_nearest_declared_name_is_offered_and_ties_are_all_offered()
	{
		var schema = Schema("""{"filePath":{"type":"string"},"projectPath":{"type":"string"},"line":{"type":"integer"},"lines":{"type":"integer"}}""");

		var undeclared = ToolArgumentShape.Undeclared(schema, Arguments("""{"path":"a","lien":1,"FilePath":"b"}"""));

		undeclared.Select(argument => argument.Name).ShouldBe(["path", "lien", "FilePath"]);
		undeclared[0].Closest.ShouldBe(["filePath"]);
		undeclared[1].Closest.ShouldBe(["line"]);
		undeclared[2].Closest.ShouldBe(["filePath"]);
	}

	/// <summary>
	/// A typo is close by edits alone: a swapped pair of letters counts once, as a hand makes it.
	/// </summary>
	[Test]
	public void A_typo_is_offered_its_spelling()
	{
		var undeclared = ToolArgumentShape.Undeclared(Outline, Arguments("""{"symbl":"A","fliePath":"b","includeInheritd":true}"""));

		undeclared.Select(argument => argument.Closest.Single()).ShouldBe(["symbol", "filePath", "includeInherited"]);
	}

	/// <summary>
	/// A name close to nothing is not matched to something anyway. The caller is given the list of
	/// names the tool does take instead, since one that guessed wrong once will guess again without it.
	/// </summary>
	[Test]
	public void Nothing_close_offers_every_name_the_tool_takes()
	{
		var undeclared = ToolArgumentShape.Undeclared(Outline, Arguments("""{"type":"A.B"}"""));
		undeclared.Single().Closest.ShouldBeEmpty();

		ToolArgumentShape.NotArguments("rose_outline", Outline, Arguments("""{"type":"A.B"}"""))
			.ShouldBe(
				"`type` is not an argument of `rose_outline`. It takes `symbol`, `filePath`, "
					+ "`includeInherited` and `workspace`.");
	}

	/// <summary>A tool that declares no arguments says so rather than listing nothing.</summary>
	[Test]
	public void A_tool_with_no_arguments_says_it_takes_none()
	{
		ToolArgumentShape.Ignored("rose_debug_list", Schema("{}"), Arguments("""{"workspace":"A.slnx"}"""))
			.ShouldBe(["Ignored an argument called `workspace`; `rose_debug_list` has no such argument. `rose_debug_list` takes no arguments."]);
	}

	/// <summary>
	/// Nothing is said where every argument is declared, where there are no arguments, or where the
	/// schema has no properties this can read -- a schema this cannot read is no evidence that a name
	/// is wrong, and the refusal keeps its own words.
	/// </summary>
	[Test]
	public void Says_nothing_where_every_name_is_declared_or_the_schema_cannot_say()
	{
		ToolArgumentShape.Undeclared(Outline, Arguments("""{"symbol":"A","filePath":null}""")).ShouldBeEmpty();
		ToolArgumentShape.Undeclared(Outline, null).ShouldBeEmpty();
		ToolArgumentShape.Undeclared(JsonDocument.Parse("""{"type":"object"}""").RootElement, Arguments("""{"file":"a"}""")).ShouldBeEmpty();
		ToolArgumentShape.Undeclared(JsonDocument.Parse("null").RootElement, Arguments("""{"file":"a"}""")).ShouldBeEmpty();

		ToolArgumentShape.NotArguments("rose_outline", Outline, Arguments("""{"symbol":"A"}""")).ShouldBeNull();
		ToolArgumentShape.Refusal("Nothing is called A.", false, "rose_outline", Outline, Arguments("""{"symbol":"A"}"""))
			.ShouldBe("Nothing is called A.");
	}

	/// <summary>
	/// The binder's own refusal is still replaced by the argument whose shape was wrong, and a
	/// misspelled name in the same call is named after it. A refusal that does not end a sentence is
	/// ended before the next one starts.
	/// </summary>
	[Test]
	public void A_binder_refusal_names_the_shape_and_then_the_name()
	{
		var schema = Schema("""{"symbol":{"type":"string"},"usings":{"type":["array","null"],"items":{"type":"string"}}}""");

		var message = ToolArgumentShape.Refusal(
			"The JSON value could not be converted to System.String[]. Path: $",
			binderRefused: true,
			"rose_add_member",
			schema,
			Arguments("""{"symbol":"A","usings":"System.Text","sybmol":"B"}"""));

		message.ShouldStartWith("usings takes a list of strings", Case.Sensitive);
		message.ShouldEndWith("`sybmol` is not an argument of `rose_add_member`. Did you mean `symbol`?", Case.Sensitive);

		ToolArgumentShape.Refusal("No solution was found near D:/a", false, "rose_outline", Outline, Arguments("""{"file":"a"}"""))
			.ShouldStartWith("No solution was found near D:/a. `file`", Case.Sensitive);
	}

	/// <summary>
	/// A list of objects sent with a bare string inside it. The outer shape is right, so the outer
	/// check alone would pass it over and leave the binder's account, which names a CLR type and a
	/// JSON path; the entry is named instead, with an example entry carrying the property it needs.
	/// </summary>
	[Test]
	public void Names_the_entry_of_a_list_of_objects_that_is_not_an_object()
	{
		var message = ToolArgumentShape.Mismatch(
			AddTracepoint,
			Arguments("""{"sessionId":"s","tracepoints":[{"location":"A.B.C"},"A.B.D"]}"""));

		message.ShouldNotBeNull();
		message.ShouldStartWith("tracepoints takes a list of objects, and tracepoints[1] is a string", Case.Sensitive);
		message.ShouldContain("""[{"location": "..."}]""", Case.Sensitive);
		message.ShouldNotContain("AddTracepointRequest", Case.Sensitive);
	}

	/// <summary>An entry that leaves out what every entry needs is named, and so is what it left out.</summary>
	[Test]
	public void Names_the_entry_that_leaves_out_a_required_property()
	{
		var message = ToolArgumentShape.Mismatch(
			AddTracepoint,
			Arguments("""{"sessionId":"s","tracepoints":[{"location":"A.B.C"},{"logMessage":"x"}]}"""));

		message.ShouldBe("tracepoints[1] has no location, which every entry of tracepoints needs.");
	}

	/// <summary>
	/// One entry sent bare, where a list of them was wanted: the shape a caller sends when it reaches
	/// for a single tracepoint. The example shows the brackets around an entry.
	/// </summary>
	[Test]
	public void A_bare_object_for_a_list_of_objects_is_shown_the_brackets()
	{
		var message = ToolArgumentShape.Mismatch(
			AddTracepoint,
			Arguments("""{"sessionId":"s","tracepoints":{"location":"A.B.C"}}"""));

		message.ShouldBe("""tracepoints takes a list of objects, and an object was sent. Send it as [{"location": "..."}].""");
	}

	/// <summary>Entries that all match say nothing, so a refusal for some other reason keeps its own words.</summary>
	[Test]
	public void Says_nothing_when_every_entry_matches()
	{
		ToolArgumentShape.Mismatch(
			AddTracepoint,
			Arguments("""{"sessionId":"s","tracepoints":[{"location":"A.B.C"},{"location":"A.B.D","logMessage":"x"}]}"""))
			.ShouldBeNull();
	}

	/// <summary>
	/// The schema rose_debug_add_tracepoint is actually listed with, built from its declaration rather
	/// than written out by hand, so the required list the entry check reads is the one the SDK emits.
	/// </summary>
	private static readonly JsonElement AddTracepoint = McpServerTool.Create(
		typeof(LiveAppDebugTools).GetMethod(nameof(LiveAppDebugTools.AddTracepointAsync))!,
		_ => throw new InvalidOperationException("Only the schema is read.")).ProtocolTool.InputSchema;

	private static readonly JsonElement Outline = Schema(
		"""{"symbol":{"type":["string","null"]},"filePath":{"type":["string","null"]},"includeInherited":{"type":"boolean"},"workspace":{"type":["string","null"]}}""");

	private static JsonElement Schema(string properties) =>
		JsonDocument.Parse($$"""{"type":"object","properties":{{properties}}}""").RootElement;

	private static IReadOnlyDictionary<string, JsonElement> Arguments(string json) =>
		JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(
			property => property.Name,
			property => property.Value,
			StringComparer.Ordinal);
}
