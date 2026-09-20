using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp;

/// <summary>
/// Writes this session's event cursor onto every answer the host sends, so a caller that acts and
/// then waits is holding the position its action happened at without having gone to ask for one.
/// <para>
/// One filter rather than a line in each tool, for the reason the broker adds workspace attribution
/// in one place: what a tool never has to remember, a tool added later cannot forget. The other half
/// of that guarantee is <see cref="LiveResult"/> -- a result type that does not derive from it drops
/// the number when the broker deserializes the answer, which is what the parity test checks.
/// </para>
/// <para>
/// Written into the serialized answer rather than onto the object, because the seam that catches
/// every tool is the one the protocol goes through: a filter sees the result whatever produced it,
/// while a helper has to be called. Rebuilt through a node tree because a <see cref="JsonElement"/>
/// cannot be added to, the same dance <c>ToolListing</c> does to the input schema.
/// </para>
/// </summary>
internal static class CursorStamp
{
	/// <summary>The property as the wire spells it, which is how the answer is addressed here.</summary>
	private const string Cursor = "cursor";

	/// <summary>
	/// The call-tool filter that stamps the cursor. An answer with no structured content -- a refusal,
	/// or a tool that returns none -- is passed through untouched: there is nothing there to say it in,
	/// and inventing an object to hold it would turn a failure into something that parses.
	/// </summary>
	internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter =>
		next => async (context, cancellationToken) =>
		{
			var result = await next(context, cancellationToken);

			if (result.StructuredContent is not { } content) return result;
			if (context.Services?.GetService<LiveAppSessionHost>() is not { } host) return result;
			if (JsonNode.Parse(content.GetRawText()) is not JsonObject answer) return result;

			answer[Cursor] = host.EventCursor;
			result.StructuredContent = JsonSerializer.SerializeToElement(answer);

			return result;
		};
}
