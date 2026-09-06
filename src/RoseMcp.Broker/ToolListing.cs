using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace RoseMcp.Broker;

/// <summary>
/// What a client is sent for a tool, which is less than the SDK generates for it.
/// <para>
/// Separate from the filter that applies it so the surface can be measured without a server: what
/// the listing carries is a decision about the context budget of every session, and the wiring that
/// carries it is one line.
/// </para>
/// </summary>
public static class ToolListing
{
	/// <summary>
	/// Trims one tool to what a model reads.
	/// <para>
	/// The output schema is a third of the wire. The SDK generates one per tool from the return type,
	/// because every tool asks for structured content, and they carry no prose at all -- not one
	/// <c>description</c> between them, so a model that did read one would learn field names and
	/// nothing about what they mean. Cleared here rather than by turning the attribute flag off,
	/// which would also drop <c>structuredContent</c> from every result, where the shape is the
	/// useful thing.
	/// </para>
	/// <para>
	/// Line endings go the same way, in the tool text and in the parameter help alike. Both are
	/// written as raw string literals in CRLF files, so every break in them reaches the client as a
	/// carriage return, and at least one client passes those through to the model verbatim. A
	/// description is prose: where its lines end says nothing.
	/// </para>
	/// </summary>
	public static void Trim(Tool tool)
	{
		tool.OutputSchema = null;
		tool.InputSchema = Normalised(tool.InputSchema);

		if (tool.Description is { Length: > 0 } description) tool.Description = OneLineBreak(description);
	}

	/// <summary>
	/// Prose with every line ending the same, and none of them a carriage return. A blank line is a
	/// paragraph break and survives it; a single break inside a paragraph is where the author's
	/// editor wrapped.
	/// </summary>
	private static string OneLineBreak(string description) =>
		description.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

	/// <summary>
	/// The input schema with every description's endings normalised. Rewritten through a node tree
	/// rather than by editing the raw JSON, where a carriage return inside a string is escaped and a
	/// description that spells the escape out would be indistinguishable from one that carries it.
	/// </summary>
	private static JsonElement Normalised(JsonElement schema)
	{
		var root = JsonNode.Parse(schema.GetRawText());

		Normalise(root);

		return JsonSerializer.SerializeToElement(root);
	}

	/// <summary>
	/// Normalises every description under a node, at any depth: the SDK nests one per property, and
	/// a nullable or array parameter nests further still.
	/// </summary>
	private static void Normalise(JsonNode? node)
	{
		switch (node)
		{
			case JsonObject json:
				foreach (var (name, value) in json.ToArray())
				{
					if (name == "description" && value is JsonValue prose && prose.TryGetValue(out string? text))
					{
						json[name] = OneLineBreak(text);
						continue;
					}

					Normalise(value);
				}

				break;

			case JsonArray array:
				foreach (var item in array) Normalise(item);

				break;
		}
	}
}
