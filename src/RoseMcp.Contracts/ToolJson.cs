using System.Runtime.CompilerServices;
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
	private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Derived = new();

	/// <summary>
	/// The one encoder a tool's text is written with. Relaxed, which leaves every printable
	/// character as itself and escapes only what JSON requires: quotes, backslashes and control
	/// characters.
	/// </summary>
	public static JavaScriptEncoder Encoder => JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

	/// <summary>
	/// The SDK's own options with <see cref="Encoder"/> in place of its default, and everything else
	/// -- casing, null handling, the type resolvers -- as the SDK has it, so a result changes
	/// spelling and nothing else.
	/// <para>
	/// The same instance for the same basis, read-only, so a host can ask for it wherever it needs
	/// it and a filter that rewrites a result writes it exactly as the tool's own registration did.
	/// </para>
	/// </summary>
	/// <param name="basis">The options the SDK would otherwise use: <c>McpJsonUtilities.DefaultOptions</c>.</param>
	public static JsonSerializerOptions Readable(JsonSerializerOptions basis) =>
		Derived.GetValue(basis, static basis =>
		{
			var options = new JsonSerializerOptions(basis) { Encoder = Encoder };
			options.MakeReadOnly();

			return options;
		});
}
