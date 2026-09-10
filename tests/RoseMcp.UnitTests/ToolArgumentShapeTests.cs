using System.Text.Json;
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

		Assert.NotNull(message);
		Assert.StartsWith("arguments takes a list of strings", message, StringComparison.Ordinal);
		Assert.Contains("a string was sent", message!, StringComparison.Ordinal);
		Assert.Contains("[\"one\", \"two\"]", message, StringComparison.Ordinal);
		Assert.DoesNotContain("System.String", message, StringComparison.Ordinal);
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

		Assert.NotNull(message);
		Assert.StartsWith("apply takes a boolean", message, StringComparison.Ordinal);
		Assert.Contains("true or false", message!, StringComparison.Ordinal);
	}

	/// <summary>
	/// A nullable argument is declared as a union with null in it, and null is always allowed -- so
	/// sending one is not the mismatch, whatever else the call was refused for.
	/// </summary>
	[Test]
	public void Says_nothing_about_a_null_sent_for_a_nullable_argument()
	{
		Assert.Null(ToolArgumentShape.Mismatch(
			Schema("""{"workspace":{"type":["string","null"]}}"""),
			Arguments("""{"workspace":null}""")));
	}

	/// <summary>
	/// Nothing is said where every argument matches its declared type. This runs only after the
	/// binder has already refused a call, and a refusal it cannot explain has to fall back to the
	/// binder's own words rather than name an argument that is fine.
	/// </summary>
	[Test]
	public void Says_nothing_when_every_argument_matches()
	{
		Assert.Null(ToolArgumentShape.Mismatch(
			Schema("""{"symbol":{"type":"string"},"usings":{"type":["array","null"],"items":{"type":"string"}}}"""),
			Arguments("""{"symbol":"A.B","usings":["System.Text"]}""")));
	}

	/// <summary>
	/// An argument the schema does not declare, and a schema with no properties at all: both are
	/// shapes this cannot judge, and judging them anyway would refuse a call for the wrong reason.
	/// </summary>
	[Test]
	public void Says_nothing_about_what_the_schema_does_not_declare()
	{
		Assert.Null(ToolArgumentShape.Mismatch(
			Schema("""{"symbol":{"type":"string"}}"""),
			Arguments("""{"unknown":42}""")));

		Assert.Null(ToolArgumentShape.Mismatch(
			JsonDocument.Parse("""{"type":"object"}""").RootElement,
			Arguments("""{"symbol":42}""")));
	}

	private static JsonElement Schema(string properties) =>
		JsonDocument.Parse($$"""{"type":"object","properties":{{properties}}}""").RootElement;

	private static IReadOnlyDictionary<string, JsonElement> Arguments(string json) =>
		JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(
			property => property.Name,
			property => property.Value,
			StringComparer.Ordinal);
}
