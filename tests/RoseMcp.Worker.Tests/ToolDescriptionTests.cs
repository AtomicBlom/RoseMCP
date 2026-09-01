using System.ComponentModel;
using System.Reflection;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Tests;

/// <summary>
/// What a tool says about itself is the only thing read once it is already under consideration, so a
/// description that is wrong or thin is a tool that loses to grep for a reason nobody can see.
/// <para>
/// These guard the two ways that goes wrong quietly. The hosts each declare the same tools, and
/// their descriptions had already drifted -- five of them, with the broker's copy, the one an MCP
/// client actually reads, saying less every time. And a new tool can be shipped with a description
/// that says what it does and never says why to use it.
/// </para>
/// </summary>
public sealed class ToolDescriptionTests
{
	[Fact]
	public void The_two_hosts_describe_every_shared_tool_identically()
	{
		var broker = Describe(typeof(RoseMcp.Broker.Tools.BrokerTools).Assembly);
		var worker = Describe(typeof(WorkspaceHost).Assembly);

		var shared = broker.Keys.Intersect(worker.Keys, StringComparer.Ordinal).ToArray();

		Assert.NotEmpty(shared);

		foreach (var name in shared)
		{
			Assert.Equal(broker[name], worker[name]);
		}
	}

	/// <summary>
	/// Every tool the broker exposes has to be described, and at a length that can carry a reason.
	/// A one-liner naming the operation is the shape a description takes when nobody asked what the
	/// caller would otherwise have done instead.
	/// </summary>
	[Fact]
	public void Every_tool_says_more_than_its_own_name()
	{
		foreach (var (name, description) in Describe(typeof(RoseMcp.Broker.Tools.BrokerTools).Assembly))
		{
			Assert.False(string.IsNullOrWhiteSpace(description), $"{name} has no description");
			Assert.True(description.Length > 120, $"{name} is described in {description.Length} characters");
		}
	}

	/// <summary>
	/// Naming the alternative is the whole job. Each of these tools exists because a caller would
	/// otherwise reach for something that quietly does not work, and the description is the only
	/// place that can say so before the choice is made.
	/// </summary>
	[Theory]
	[InlineData(ToolNames.FindReferences, "text search")]
	[InlineData(ToolNames.FindImplementations, "Grep cannot")]
	[InlineData(ToolNames.RenameSymbol, "find-and-replace")]
	[InlineData(ToolNames.MoveTypeToFile, "rather than reading a file and writing two")]
	[InlineData(ToolNames.FormatDocuments, "by any other means")]
	[InlineData(ToolNames.ApplyCodeFix, "rather than editing each occurrence")]
	[InlineData(ToolNames.Diagnostics, "Prefer this to")]
	[InlineData(ToolNames.ListGeneratedDocuments, "no file search")]
	[InlineData(ToolNames.ReadGeneratedDocument, "no other way")]
	public void Says_what_the_caller_would_otherwise_have_done(string tool, string expected)
	{
		var descriptions = Describe(typeof(RoseMcp.Broker.Tools.BrokerTools).Assembly);

		Assert.Contains(expected, descriptions[tool], StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The one failure a caller cannot diagnose from the symptom: a solution loaded under a
	/// configuration it does not declare resolves no references, and every file reports that
	/// System.Object is missing. Status is where that has to be findable.
	/// </summary>
	[Fact]
	public void Status_points_at_the_configuration_when_everything_looks_broken()
	{
		var status = Describe(typeof(RoseMcp.Broker.Tools.BrokerTools).Assembly)[ToolNames.WorkspaceStatus];

		Assert.Contains("configuration", status, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("degraded", status, StringComparison.OrdinalIgnoreCase);
	}

	private static Dictionary<string, string> Describe(Assembly assembly)
	{
		var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var type in assembly.GetTypes())
		{
			if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is not { Name: { Length: > 0 } name }) continue;

				descriptions[name] = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
			}
		}

		return descriptions;
	}
}
