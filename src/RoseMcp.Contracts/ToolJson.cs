using System.Text.Encodings.Web;
using System.Text.Json;

namespace RoseMcp.Contracts;

/// <summary>
/// How a tool's result is written as JSON, in every host that registers tools.
/// <para>
/// A typed tool's answer reaches a client twice: as structured content, and as a text block holding
/// the same JSON, which is the form most clients hand the model. The SDK writes that text with the
/// framework's default encoder, which escapes <c>+ &lt; &gt; " ' `</c> and <c>&amp;</c> as
/// <c>\u002B</c> and the like -- safe for embedding in HTML, which nothing here does, and ruinous
/// for a diff, where every added line starts with one. The escaping is in the string itself rather
/// than in the frame around it, so no client decodes it.
/// </para>
/// <para>
/// One encoder, chosen here, because a host that writes some results one way and some another
/// answers the same question in two spellings within one session: a result rewritten by a filter
/// would read cleanly beside one the SDK wrote, and nothing about the call would say why. Here
/// rather than in each host because the broker, the worker and the live-app host each register
/// tools and none of them references another.
/// </para>
/// </summary>
public static class ToolJson
{
	/// <summary>
	/// The one encoder a tool's text is written with. Relaxed, which leaves every character of the
	/// Basic Multilingual Plane as itself apart from what JSON requires escaped -- quotes,
	/// backslashes and control characters. A character outside that plane, an emoji for one, is
	/// still written as an escaped surrogate pair.
	/// </summary>
	public static JavaScriptEncoder Encoder => JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

	/// <summary>
	/// A read-only copy of the SDK's own options with <see cref="Encoder"/> in place of its default,
	/// and everything else -- casing, null handling, the type resolvers -- as the SDK has it, so a
	/// result changes spelling and nothing else.
	/// <para>
	/// A new instance on every call. A host that registers several tool types, or rewrites a result
	/// after the SDK wrote it, keeps one in a field of its own and uses it for both.
	/// </para>
	/// </summary>
	/// <param name="basis">The options the SDK would otherwise use: <c>McpJsonUtilities.DefaultOptions</c>.</param>
	public static JsonSerializerOptions Readable(JsonSerializerOptions basis)
	{
		var options = new JsonSerializerOptions(basis) { Encoder = Encoder };
		options.MakeReadOnly();

		return options;
	}
}
