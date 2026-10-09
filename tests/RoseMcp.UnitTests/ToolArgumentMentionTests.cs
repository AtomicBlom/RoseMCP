using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// An argument a tool's own text names is one the tool declares. A caller follows the text literally:
/// told "one of this and symbol" by a tool that takes <c>symbols</c>, it sends <c>symbol</c>, which the
/// binder drops, and the call is refused for want of what it was told to send.
/// </summary>
public sealed partial class ToolArgumentMentionTests
{
	/// <summary>
	/// Every argument an argument's help offers as its alternative -- "one of this and X" -- is declared
	/// by the same tool.
	/// </summary>
	[Test]
	public void The_alternative_an_argument_names_is_one_its_tool_declares()
	{
		foreach (var tool in Listed())
		{
			var declared = Declared(tool);

			foreach (var help in Helps(tool))
			{
				foreach (Match alternative in OneOfThisAnd().Matches(help))
				{
					declared.ShouldContain(
						alternative.Groups["name"].Value,
						$"{tool.Name}'s help says '{alternative.Value}', and {tool.Name} declares no such argument");
				}
			}
		}
	}

	/// <summary>
	/// Every argument name that the surface declares somewhere and a tool's description or help spells,
	/// in its camelCase form, is declared by that tool -- so a description cannot keep naming an argument
	/// the tool has renamed. Single words are left out: <c>symbol</c> and <c>project</c> are nouns far
	/// more often than they are arguments.
	/// </summary>
	[Test]
	public void Every_argument_a_tool_names_in_its_own_text_is_one_it_declares()
	{
		var tools = Listed();
		var surface = tools.SelectMany(Declared).Where(name => name.Any(char.IsUpper)).ToHashSet(StringComparer.Ordinal);

		foreach (var tool in tools)
		{
			var declared = Declared(tool);
			var text = string.Join(" ", [tool.Description ?? "", .. Helps(tool)]);

			foreach (Match word in CamelCase().Matches(text))
			{
				var named = word.Value;
				if (!surface.Contains(named)) continue;

				declared.ShouldContain(named, $"{tool.Name}'s text names {named}, which it does not declare");
			}
		}
	}

	private static IReadOnlySet<string> Declared(Tool tool) =>
		tool.InputSchema.TryGetProperty("properties", out var properties)
			? properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
			: new HashSet<string>();

	private static IEnumerable<string> Helps(Tool tool)
	{
		if (!tool.InputSchema.TryGetProperty("properties", out var properties)) yield break;

		foreach (var property in properties.EnumerateObject())
		{
			if (property.Value.TryGetProperty("description", out var help) && help.ValueKind == JsonValueKind.String)
			{
				yield return help.GetString() ?? "";
			}
		}
	}

	private static Tool[] Listed()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		return [.. provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool)];
	}

	[GeneratedRegex(@"[Oo]ne of this and (?<name>[A-Za-z]+)")]
	private static partial Regex OneOfThisAnd();

	[GeneratedRegex(@"\b[a-z]+[A-Z][A-Za-z]*\b")]
	private static partial Regex CamelCase();
}
