using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// What the surface costs a client, held to a budget.
/// <para>
/// The surface grew nineteen tools in two days and nothing measured it: 45 tools and 101 KB became
/// 52 and 134, with a 258-character workspace argument repeated thirty times and descriptions
/// carrying design rationale that already existed in the doc comments. A description is read once
/// its tool is already a candidate, so it needs the claim, the trigger and the refusals -- three to
/// five sentences -- and the reasoning belongs where a maintainer reads it.
/// </para>
/// <para>
/// The numbers are ceilings, not targets. The targets are 500 characters for a read tool and 800
/// for a write one; several tools sit above the read target on purpose, because a measured caveat
/// that a caller has to know -- reading a XAML element materialises its collection properties --
/// is worth more than the characters it costs. What a ceiling buys is that the next tool cannot
/// quietly add a kilobyte.
/// </para>
/// </summary>
public sealed class ToolBudgetTests
{
	/// <summary>
	/// The hard ceiling on one description. Nothing needs more than this; two tools were over it
	/// and one was at 1,451.
	/// </summary>
	private const int PerTool = 1000;

	/// <summary>
	/// The ceiling on one argument's help. The target is 120; the ones above it carry a refusal or
	/// a default a caller cannot guess, and the reasoning behind each sits in a doc comment.
	/// </summary>
	private const int PerParameter = 250;

	/// <summary>
	/// Everything the model is shown, across every tool: the descriptions plus the input schemas.
	/// It was 76,241 before this budget existed.
	/// </summary>
	private const int ModelFacing = 74000;

	[Test]
	public void No_description_is_longer_than_its_ceiling()
	{
		foreach (var tool in Listed())
		{
			var length = tool.Description?.Length ?? 0;

			Assert.True(length <= PerTool, $"{tool.Name} is described in {length} characters");
			Assert.True(length > 120, $"{tool.Name} is described in {length} characters, which cannot carry a reason");
		}
	}

	[Test]
	public void No_argument_help_is_longer_than_its_ceiling()
	{
		foreach (var tool in Listed())
		{
			foreach (var (name, length) in Arguments(tool))
			{
				Assert.True(length <= PerParameter, $"{tool.Name}'s {name} is described in {length} characters");
			}
		}
	}

	/// <summary>
	/// And the total, because a per-tool ceiling says nothing about fifty-two of them. This is the
	/// number a client without tool deferral pays at the start of every session.
	/// </summary>
	[Test]
	public void The_whole_model_facing_surface_stays_within_its_budget()
	{
		var total = Listed().Sum(tool => (tool.Description?.Length ?? 0) + tool.InputSchema.GetRawText().Length);

		Assert.InRange(total, 1, ModelFacing);
	}

	/// <summary>Each argument's name and how long its help is, from the schema as it is sent.</summary>
	private static IEnumerable<(string Name, int Length)> Arguments(Tool tool)
	{
		if (!tool.InputSchema.TryGetProperty("properties", out var properties)) yield break;

		foreach (var property in properties.EnumerateObject())
		{
			if (property.Value.TryGetProperty("description", out var described)
				&& described.ValueKind == JsonValueKind.String)
			{
				yield return (property.Name, described.GetString()?.Length ?? 0);
			}
		}
	}

	/// <summary>
	/// The tools as a client is sent them: the registration with the listing's own trim applied,
	/// since that is what decides the size.
	/// </summary>
	private static Tool[] Listed()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		var tools = provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool).ToArray();

		foreach (var tool in tools) ToolListing.Trim(tool);

		return tools;
	}
}
