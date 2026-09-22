using System.Reflection;
using System.Text.Json;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoseMcp.Broker;

/// <summary>
/// The alternative argument spellings each tool accepts, read off the tool declarations once and
/// applied to a call before its arguments bind.
/// <para>
/// This exists because an argument the schema does not declare is dropped rather than refused. A
/// call naming <c>solution</c> instead of <c>workspace</c> loses the only thing saying where it was
/// meant to go, and the broker then answers from whatever the working directory implies -- with a
/// revision, a workspace name and every other mark of an answer about the right solution. Nothing
/// in the result says an argument went missing, which makes it the failure shape the routing rules
/// exist to prevent rather than a caller's mistake they catch.
/// </para>
/// <para>
/// Built from the same types that register the tools, so a tool added later cannot arrive with its
/// aliases unread. A mis-declared alias throws here, at startup, rather than being tolerated per
/// call: an alias that shadows a real parameter is a programming error, and one that surfaces on
/// the call it corrupts has already cost the thing it was meant to protect.
/// </para>
/// </summary>
public sealed class ArgumentAliases
{
	private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byTool;
	private readonly IReadOnlySet<string> _scanned;

	private ArgumentAliases(
		IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byTool,
		IReadOnlySet<string> scanned)
	{
		_byTool = byTool;
		_scanned = scanned;
	}

	/// <summary>No tool accepts anything but its own parameter names.</summary>
	public static ArgumentAliases None { get; } = new(
		new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
		new HashSet<string>(StringComparer.Ordinal));

	/// <summary>Every tool that declares at least one alias.</summary>
	public IEnumerable<string> Tools => _byTool.Keys;

	/// <summary>
	/// Every tool whose declarations were read, aliases or not. A tool the server offers but that is
	/// missing from here was registered from a type nothing scanned, so its arguments would go on
	/// being dropped however carefully its aliases were written.
	/// </summary>
	public IReadOnlySet<string> Scanned => _scanned;

	/// <summary>
	/// Reads the aliases declared by the tools on these types.
	/// </summary>
	/// <param name="toolTypes">The types passed to <c>WithTools</c>, so the two cannot disagree.</param>
	/// <exception cref="InvalidOperationException">
	/// An alias shadows a parameter the same tool declares, or two of its parameters claim the same
	/// one. Both mean a call would bind somewhere the caller did not name.
	/// </exception>
	public static ArgumentAliases From(IEnumerable<Type> toolTypes)
	{
		var byTool = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
		var scanned = new HashSet<string>(StringComparer.Ordinal);

		foreach (var method in toolTypes.SelectMany(type => type.GetMethods(Declared)))
		{
			if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } tool) continue;

			var name = tool.Name ?? method.Name;
			scanned.Add(name);
			var parameters = method.GetParameters();
			var declared = parameters.Select(parameter => parameter.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
			var aliases = new Dictionary<string, string>(StringComparer.Ordinal);

			foreach (var parameter in parameters)
			{
				if (parameter.Name is not { Length: > 0 } canonical) continue;

				foreach (var alias in parameter.GetCustomAttributes<ArgumentAliasAttribute>().Select(attribute => attribute.Alias))
				{
					if (declared.Contains(alias))
					{
						throw new InvalidOperationException(
							$"{name} declares a parameter called '{alias}', so it cannot also be an alias for "
								+ $"'{canonical}'. A call naming it would bind to whichever the filter reached first.");
					}

					if (!aliases.TryAdd(alias, canonical))
					{
						throw new InvalidOperationException(
							$"{name} claims '{alias}' as an alias for both '{aliases[alias]}' and '{canonical}'. "
								+ "One spelling cannot stand for two arguments.");
					}
				}
			}

			if (aliases.Count > 0) byTool[name] = aliases;
		}

		return new ArgumentAliases(byTool, scanned);
	}

	/// <summary>What one tool accepts in place of its own parameter names, as alias to parameter.</summary>
	public IReadOnlyDictionary<string, string> For(string tool) =>
		_byTool.TryGetValue(tool, out var aliases) ? aliases : ReadOnlyEmpty;

	/// <summary>
	/// Reads a call's arguments and says what should be bound instead, if anything.
	/// <para>
	/// Taking the parameters rather than the request so the decision is testable without an MCP
	/// server to raise one, which is the whole of the logic worth testing.
	/// </para>
	/// </summary>
	public AliasCorrection Read(string tool, IDictionary<string, JsonElement>? arguments)
	{
		if (arguments is null || arguments.Count == 0) return AliasCorrection.Nothing;

		var aliases = For(tool);
		if (aliases.Count == 0) return AliasCorrection.Nothing;

		var applied = new List<string>();

		foreach (var (alias, canonical) in aliases)
		{
			if (!arguments.ContainsKey(alias)) continue;

			// Both spellings, and nothing here can know which the caller meant. Picking one is the
			// guess this whole file exists to remove, and a solution named twice over is exactly
			// where guessing costs most.
			if (arguments.ContainsKey(canonical))
			{
				return AliasCorrection.Refusing(
					$"{tool} was given both '{canonical}' and '{alias}', which name the same argument. "
						+ $"Send only '{canonical}'.");
			}

			applied.Add(alias);
		}

		if (applied.Count == 0) return AliasCorrection.Nothing;

		var corrected = new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);

		foreach (var alias in applied)
		{
			corrected[aliases[alias]] = corrected[alias];
			corrected.Remove(alias);
		}

		return AliasCorrection.Rewriting(corrected, [.. applied.Select(alias => $"{alias} -> {aliases[alias]}")]);
	}

	/// <summary>The tool a call matched, by the name the server knows it by rather than the one sent.</summary>
	public static string NameOf(RequestContext<CallToolRequestParams> context) =>
		context.MatchedPrimitive is McpServerTool tool ? tool.ProtocolTool.Name : context.Params?.Name ?? string.Empty;

	private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

	private static readonly IReadOnlyDictionary<string, string> ReadOnlyEmpty =
		new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>What reading a call's arguments against one tool's aliases came to.</summary>
public sealed record AliasCorrection
{
	/// <summary>Nothing was aliased, so the call binds what it was sent.</summary>
	public static AliasCorrection Nothing { get; } = new();

	/// <summary>The arguments to bind in place of the ones sent, or null when none were rewritten.</summary>
	public IDictionary<string, JsonElement>? Arguments { get; private init; }

	/// <summary>Why the call cannot proceed, or null when it can.</summary>
	public string? Refusal { get; private init; }

	/// <summary>Each rewrite as <c>alias -&gt; parameter</c>, for the log that says which spellings callers reach for.</summary>
	public IReadOnlyList<string> Applied { get; private init; } = [];

	internal static AliasCorrection Refusing(string refusal) => new() { Refusal = refusal };

	internal static AliasCorrection Rewriting(IDictionary<string, JsonElement> arguments, IReadOnlyList<string> applied) =>
		new() { Arguments = arguments, Applied = applied };
}
