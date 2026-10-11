using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace RoseMcp.Server;

/// <summary>
/// A broker's tool definitions and server instructions, reduced to what tells two of them apart.
/// <para>
/// It is what decides whether a client hears about a restart at the protocol level. The broker
/// restarts on every deploy, because the code behind a tool can change while its definition does
/// not -- but a client holds only the definitions, so where those and the instructions are the same
/// it has nothing to refresh, and telling it to would cost every session a tool-list reload for
/// nothing. Where they differ it has to be told, or it goes on calling with arguments the new
/// definitions no longer take.
/// </para>
/// <para>
/// Each tool is compared as canonical JSON -- the SDK's own serialisation, object keys sorted, its
/// <c>_meta</c> left out -- so two builds that emit the same schema with properties in another order
/// compare equal, and a change to a description, a schema or an annotation does not. Instructions
/// are compared with their line endings normalised, for the same reason the listing normalises them.
/// </para>
/// </summary>
public sealed class ToolSurface
{
	/// <summary>Each tool's name and a hash of its canonical definition, by name.</summary>
	private readonly IReadOnlyDictionary<string, string> _tools;

	private ToolSurface(IReadOnlyDictionary<string, string> tools, string? instructions)
	{
		_tools = tools;
		Instructions = instructions;

		var lines = tools
			.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => $"{pair.Key}={pair.Value}")
			.Append($"instructions={Hash(Normalised(instructions) ?? string.Empty)}");

		Fingerprint = Hash(string.Join('\n', lines));
	}

	/// <summary>The server instructions this surface carries, as the broker sent them.</summary>
	public string? Instructions { get; }

	/// <summary>A hash of every tool definition and the instructions together: equal exactly when nothing a client holds differs.</summary>
	public string Fingerprint { get; }

	/// <summary>The surface of <paramref name="tools"/> and <paramref name="instructions"/>. A name listed twice keeps its last definition.</summary>
	public static ToolSurface Of(IEnumerable<Tool> tools, string? instructions)
	{
		var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var tool in tools) hashes[tool.Name] = Hash(Canonical(tool));

		return new ToolSurface(hashes, instructions);
	}

	/// <summary>Whether a tool of that name is part of this surface.</summary>
	public bool Declares(string tool) => _tools.ContainsKey(tool);

	/// <summary>The same tools, with other instructions.</summary>
	public ToolSurface WithInstructions(string? instructions) => new(_tools, instructions);

	/// <summary>What a client holding this surface would find different in <paramref name="newer"/>.</summary>
	public ToolSurfaceChange ChangesTo(ToolSurface newer)
	{
		var added = newer._tools.Keys.Where(name => !_tools.ContainsKey(name));
		var removed = _tools.Keys.Where(name => !newer._tools.ContainsKey(name));
		var changed = _tools
			.Where(pair => newer._tools.TryGetValue(pair.Key, out var hash) && !string.Equals(hash, pair.Value, StringComparison.Ordinal))
			.Select(pair => pair.Key);

		return new ToolSurfaceChange(
			Sorted(added),
			Sorted(removed),
			Sorted(changed),
			!string.Equals(Normalised(Instructions), Normalised(newer.Instructions), StringComparison.Ordinal));
	}

	private static IReadOnlyList<string> Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

	/// <summary>A tool as canonical JSON: the SDK's serialisation with its <c>_meta</c> removed and every object's keys in order.</summary>
	private static string Canonical(Tool tool)
	{
		var node = JsonSerializer.SerializeToNode(tool, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool)));
		if (node is JsonObject definition) definition.Remove("_meta");

		return Ordered(node)?.ToJsonString() ?? string.Empty;
	}

	/// <summary>
	/// A copy of a JSON tree with every object's properties sorted by name. Arrays keep their order,
	/// which is part of what they say. Built fresh rather than rearranged, since a node cannot be
	/// moved under a second parent.
	/// </summary>
	private static JsonNode? Ordered(JsonNode? node) => node switch
	{
		JsonObject json => new JsonObject(json
			.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => KeyValuePair.Create(pair.Key, Ordered(pair.Value)))),
		JsonArray array => new JsonArray([.. array.Select(Ordered)]),
		null => null,
		_ => node.DeepClone(),
	};

	private static string? Normalised(string? text) =>
		text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

	private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
