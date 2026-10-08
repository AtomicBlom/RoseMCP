using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Tells a caller whose call succeeded which of its arguments the tool never saw.
/// <para>
/// An argument the schema does not declare is dropped by the binder, and the call runs with the
/// declared argument it was meant for left at its default. <c>rose_find_references(symbol: "X",
/// path: "A.cs")</c> searches the whole solution and returns a correct-looking answer to a question
/// the caller did not ask, with nothing in it saying the file was set aside. The refusal path names
/// such an argument at the error boundary; this is the same schema read, for the calls that do not
/// fail.
/// </para>
/// <para>
/// In the broker alone, because the broker is the only process that sees what the caller sent.
/// What it forwards to a worker or a live-app host is built from its own declared parameters, so
/// the same check there could only ever be about the broker's call, and a relay in front of a tray
/// has no schema to read and adds nothing -- the tray's broker has already said it.
/// </para>
/// </summary>
public static class IgnoredArguments
{
	/// <summary>
	/// Written for a person reading the text block. The default encoder escapes a backtick, which
	/// every notice uses to quote a name, so a notice read as text would show <c>`path`</c>.
	/// </summary>
	private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	/// <summary>
	/// Adds a notice to each successful call for every argument it carried that the tool does not
	/// declare.
	/// <para>
	/// Register it after anything that rewrites argument names, such as the alias filter: what it
	/// reads is what the tool bound, so a spelling the broker accepts in place of a declared name is
	/// never called ignored. A call that throws is left to the error boundary, which names the same
	/// arguments in its refusal.
	/// </para>
	/// </summary>
	public static IMcpServerBuilder WithIgnoredArgumentNotices(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			var result = await next(context, cancellationToken);
			if (context.MatchedPrimitive is not McpServerTool tool) return result;

			var ignored = ToolArgumentShape.Ignored(
				tool.ProtocolTool.Name,
				tool.ProtocolTool.InputSchema,
				context.Params?.Arguments);

			return Noticed(result, ignored);
		}));

	/// <summary>
	/// The result with these notices added, in both of the forms a client may read it.
	/// <para>
	/// In <c>structuredContent</c> they join the result's own <c>notices</c> array, which is where
	/// every result that has one already says what a caller should know about the answer, and one
	/// is added where the result has none: the listing carries no output schema, so the field is a
	/// name a caller already reads rather than a contract it breaks, and one name for one kind of
	/// remark is less surprising than a second one only these results use. The text block that
	/// mirrors the structured content is rewritten to match, so a client reading text sees the same
	/// answer. Where neither form can carry it -- a tool answering in plain text, or a notices field
	/// of some other shape -- each notice is added as a text block of its own.
	/// </para>
	/// </summary>
	/// <param name="result">What the tool returned. Changed in place, and returned.</param>
	/// <param name="notices">What to tell the caller; nothing is changed where this is empty.</param>
	public static CallToolResult Noticed(CallToolResult result, IReadOnlyList<string> notices)
	{
		if (notices.Count == 0) return result;

		var structured = result.StructuredContent is { } element ? JsonNode.Parse(element.GetRawText()) : null;
		var noticed = WithNotices(structured, notices);
		if (noticed is not null) result.StructuredContent = JsonSerializer.SerializeToElement(noticed);

		// A new list rather than edits to the old one, which may be fixed-size.
		var content = new List<ContentBlock>(result.Content.Count + notices.Count);
		var mirrored = false;

		foreach (var block in result.Content)
		{
			var isMirror = noticed is not null
				&& block is TextContentBlock text
				&& JsonNode.DeepEquals(Parsed(text.Text), structured);

			content.Add(isMirror ? new TextContentBlock { Text = noticed!.ToJsonString(Readable) } : block);
			mirrored |= isMirror;
		}

		if (!mirrored) content.AddRange(notices.Select(notice => new TextContentBlock { Text = notice }));

		result.Content = content;

		return result;
	}

	/// <summary>
	/// A copy of a result object with the notices on the end of its <c>notices</c> array, or null
	/// where it is not an object, or its <c>notices</c> is something other than an array.
	/// </summary>
	private static JsonObject? WithNotices(JsonNode? structured, IReadOnlyList<string> notices)
	{
		if (structured is not JsonObject json) return null;

		var copy = json.DeepClone().AsObject();

		switch (copy["notices"])
		{
			case null:
				copy["notices"] = new JsonArray([.. notices.Select(notice => (JsonNode?)JsonValue.Create(notice))]);
				return copy;
			case JsonArray existing:
				foreach (var notice in notices) existing.Add(notice);
				return copy;
			default:
				return null;
		}
	}

	/// <summary>Text read as JSON where it is JSON, and null where it is prose.</summary>
	private static JsonNode? Parsed(string text)
	{
		try
		{
			return JsonNode.Parse(text);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
