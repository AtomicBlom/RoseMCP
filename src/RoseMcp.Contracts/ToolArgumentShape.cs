using System.Text.Json;

namespace RoseMcp.Contracts;

/// <summary>
/// Says which argument was the wrong shape, for a call the JSON binder refused.
/// <para>
/// The binder's own account names a CLR type the caller never wrote and points at the root of the
/// document: "The JSON value could not be converted to System.String[]. Path: $". It is accurate and
/// there is nothing to act on in it -- a tool with three string arguments and one array does not say
/// which of the four was sent wrong, and the type it names is not part of the tool's vocabulary.
/// Every other refusal on this surface says what was wrong with what the caller sent and what to
/// send instead.
/// </para>
/// <para>
/// The tool's own input schema is what answers it, so nothing here has to guess: each supplied
/// argument's JSON kind is checked against the type the schema declares for it, and the first
/// disagreement is the one reported. Run only once the binder has already refused the call, so a
/// schema this cannot read, or a coercion the binder is willing to make, cannot turn a working call
/// into a refusal.
/// </para>
/// <para>
/// Here rather than in each host because there are three MCP boundaries and one is in an assembly
/// the test projects cannot reference. It is pure <c>System.Text.Json</c> over a schema and a
/// dictionary, with no dependency on the MCP packages, so it stays inside what this assembly is for.
/// </para>
/// </summary>
public static class ToolArgumentShape
{
	/// <summary>
	/// A sentence naming the argument whose shape does not match the schema, or null when every
	/// supplied argument matches and the refusal was about something else.
	/// <para>
	/// An argument sent as null is passed over rather than measured. A nullable argument declares null
	/// among its types, and an argument that is not nullable is a different refusal -- something
	/// required and missing, which the binder already says clearly.
	/// </para>
	/// </summary>
	/// <param name="inputSchema">The tool's declared input schema.</param>
	/// <param name="arguments">The arguments as they arrived, by name.</param>
	public static string? Mismatch(JsonElement inputSchema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
	{
		if (arguments is null) return null;
		if (inputSchema.ValueKind != JsonValueKind.Object) return null;
		if (!inputSchema.TryGetProperty("properties", out var properties)) return null;
		if (properties.ValueKind != JsonValueKind.Object) return null;

		foreach (var (name, value) in arguments)
		{
			if (value.ValueKind == JsonValueKind.Null) continue;
			if (!properties.TryGetProperty(name, out var declared)) continue;

			var wanted = Types(declared);
			if (wanted.Count == 0) continue;
			if (wanted.Any(type => Matches(type, value.ValueKind))) continue;

			return $"{name} takes {Article(Wanted(declared, wanted[0]))} {Wanted(declared, wanted[0])}, and "
				+ $"{Article(Sent(value.ValueKind))} {Sent(value.ValueKind)} was sent. "
				+ $"Send it as {Example(declared, wanted[0])}.";
		}

		return null;
	}

	/// <summary>
	/// The types a schema property allows. A nullable argument is declared as a union, so this is a
	/// list rather than one name, and "null" is dropped: it is always allowed and never the thing
	/// the caller got wrong.
	/// </summary>
	private static IReadOnlyList<string> Types(JsonElement declared)
	{
		if (!declared.TryGetProperty("type", out var type)) return [];

		return type.ValueKind switch
		{
			JsonValueKind.String when type.GetString() is { Length: > 0 } one and not "null" => [one],
			JsonValueKind.Array =>
			[
				.. type.EnumerateArray()
					.Where(entry => entry.ValueKind == JsonValueKind.String)
					.Select(entry => entry.GetString())
					.OfType<string>()
					.Where(entry => entry != "null"),
			],
			_ => [],
		};
	}

	/// <summary>Whether a value of this JSON kind satisfies a schema type.</summary>
	private static bool Matches(string type, JsonValueKind kind) => type switch
	{
		"array" => kind == JsonValueKind.Array,
		"object" => kind == JsonValueKind.Object,
		"string" => kind == JsonValueKind.String,
		"integer" or "number" => kind == JsonValueKind.Number,
		"boolean" => kind is JsonValueKind.True or JsonValueKind.False,

		// A type this does not know is not a mismatch. Being wrong about the schema would name an
		// argument that is fine, for a call the binder refused for some other reason entirely.
		_ => true,
	};

	/// <summary>The wanted type as the caller would say it, with an array's element type named.</summary>
	private static string Wanted(JsonElement declared, string type)
	{
		if (type != "array") return type;

		var items = declared.TryGetProperty("items", out var element) ? Types(element).FirstOrDefault() : null;

		return items is null ? "list" : $"list of {items}s";
	}

	/// <summary>What arrived, in the same vocabulary.</summary>
	private static string Sent(JsonValueKind kind) => kind switch
	{
		JsonValueKind.Array => "list",
		JsonValueKind.Object => "object",
		JsonValueKind.String => "string",
		JsonValueKind.Number => "number",
		JsonValueKind.True or JsonValueKind.False => "boolean",
		_ => "null",
	};

	private static string Article(string word) =>
		word.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";

	/// <summary>
	/// What to send instead, spelled as JSON. A list is the case worth showing: the mistake is
	/// sending one element bare, and seeing the brackets is the whole correction.
	/// </summary>
	private static string Example(JsonElement declared, string type) => type switch
	{
		"array" when declared.TryGetProperty("items", out var element) && Types(element).FirstOrDefault() == "string" =>
			"[\"one\", \"two\"]",
		"array" => "a JSON array",
		"string" => "\"text\"",
		"integer" or "number" => "12",
		"boolean" => "true or false",
		_ => $"{Article(type)} {type}",
	};
}
