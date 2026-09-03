using System.ComponentModel;
using System.Reflection;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

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
	[InlineData(ToolNames.ReplaceMember, "instead of a text edit")]
	[InlineData(ToolNames.ReplaceBody, "rather than a line-range edit")]
	[InlineData(ToolNames.AddMember, "rather than finding the closing brace")]
	[InlineData(ToolNames.ChangeSignature, "an edit per layer")]
	[InlineData(ToolNames.Diagnostics, "in place of building after every change")]
	[InlineData(ToolNames.ListGeneratedDocuments, "no file search")]
	[InlineData(ToolNames.ReadGeneratedDocument, "no other way")]
	[InlineData(ToolNames.BuildFreshness, "a green build does not answer")]
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


	/// <summary>
	/// MCP requires a tool result's <c>structuredContent</c> to be a JSON <em>object</em>. A tool that
	/// returns a bare collection serialises to a top-level array instead, and a strict client rejects
	/// the whole result -- "expected record, received array" -- which does not degrade, it makes the
	/// tool uncallable. Nothing in the SDK catches it, and neither does any test that only ever calls a
	/// tool in-process, because the shape is only wrong once it has been serialised for a client.
	/// <para>
	/// It shipped on five tools: <c>rose_debug_list</c> and both list/remove pairs for tracepoints and
	/// breakpoints. The wrapper records existed and said why they existed; the broker's own tools
	/// unwrapped them again on the way out. So this asserts the property rather than the five names,
	/// since the next tool to return a list will be written by someone who has not read this.
	/// </para>
	/// </summary>
	[Fact]
	public void No_tool_returns_a_bare_collection()
	{
		foreach (var (name, returnType) in ReturnTypes(typeof(RoseMcp.Broker.Tools.BrokerTools).Assembly))
		{
			Assert.False(
				IsCollection(returnType),
				$"{name} returns {returnType.Name}, which serialises to a top-level array; "
					+ "MCP requires structuredContent to be an object, so wrap it in a record with a named property");
		}
	}

	/// <summary>The worker's tools travel the same channel to the broker, so they answer to it too.</summary>
	[Fact]
	public void No_worker_tool_returns_a_bare_collection()
	{
		foreach (var (name, returnType) in ReturnTypes(typeof(WorkspaceHost).Assembly))
		{
			Assert.False(IsCollection(returnType), $"{name} returns the collection {returnType.Name}");
		}
	}

	/// <summary>
	/// A string is a collection of characters as far as reflection is concerned, and is a perfectly
	/// good scalar result, so it is excluded deliberately rather than by accident.
	/// </summary>
	private static bool IsCollection(Type type)
	{
		if (type == typeof(string)) return false;
		if (type.IsArray) return true;

		return type.GetInterfaces().Append(type).Any(candidate =>
			candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
	}

	private static IEnumerable<(string Name, Type ReturnType)> ReturnTypes(Assembly assembly)
	{
		foreach (var type in assembly.GetTypes())
		{
			if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is not { Name: { Length: > 0 } name }) continue;

				// Unwrap Task<T> and ValueTask<T>: what a client sees is T.
				var returned = method.ReturnType;
				if (returned.IsGenericType
					&& (returned.GetGenericTypeDefinition() == typeof(Task<>)
						|| returned.GetGenericTypeDefinition() == typeof(ValueTask<>)))
				{
					returned = returned.GetGenericArguments()[0];
				}

				if (returned == typeof(Task) || returned == typeof(void)) continue;

				yield return (name, returned);
			}
		}
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
