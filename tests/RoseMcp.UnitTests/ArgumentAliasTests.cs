using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The alternative argument spellings the tools accept, checked against the surface a client is
/// actually sent rather than against the declarations in isolation.
/// <para>
/// An alias is only safe in the company of the arguments the same tool declares, so the rules worth
/// asserting are relations between the two: that no alias shadows a real argument, that each one
/// stands for an argument the tool has, and that a tool added later did not arrive without the
/// aliases its neighbours all carry.
/// </para>
/// </summary>
public sealed class ArgumentAliasTests
{
	/// <summary>
	/// The one that would be silent. An alias sharing a name with a real argument means a call
	/// naming it binds somewhere the caller did not ask for, and nothing in the result says so.
	/// </summary>
	[Test]
	public void No_alias_shadows_an_argument_the_same_tool_declares()
	{
		var (aliases, tools) = Registered();

		foreach (var tool in tools)
		{
			var declared = Arguments(tool);

			foreach (var alias in aliases.For(tool.Name).Keys)
			{
				declared.ShouldNotContain(alias);
			}
		}
	}

	/// <summary>
	/// And the other direction: an alias standing for an argument the tool does not have would be
	/// rewritten into a name the binder then drops, which is the failure it exists to prevent.
	/// </summary>
	[Test]
	public void Every_alias_stands_for_an_argument_the_tool_has()
	{
		var (aliases, tools) = Registered();

		foreach (var tool in tools)
		{
			var declared = Arguments(tool);

			foreach (var canonical in aliases.For(tool.Name).Values)
			{
				declared.ShouldContain(canonical);
			}
		}
	}

	/// <summary>
	/// Every tool that routes by workspace accepts the other name for it. A tool added later without
	/// this is one call away from answering about a different solution, so the assertion is over the
	/// whole surface rather than over a list somebody maintains.
	/// </summary>
	[Test]
	public void Every_workspace_argument_accepts_solution()
	{
		var (aliases, tools) = Registered();
		var routed = tools.Where(tool => Arguments(tool).Contains("workspace")).ToArray();

		routed.ShouldNotBeEmpty();

		foreach (var tool in routed)
		{
			aliases.For(tool.Name).GetValueOrDefault("solution").ShouldBe("workspace");
		}
	}

	/// <summary>Every file path accepts <c>file</c>, with no exceptions.</summary>
	[Test]
	public void Every_file_path_accepts_file()
	{
		var (aliases, tools) = Registered();
		var reading = tools.Where(tool => Arguments(tool).Contains("filePath")).ToArray();

		reading.ShouldNotBeEmpty();

		foreach (var tool in reading)
		{
			aliases.For(tool.Name).GetValueOrDefault("file").ShouldBe("filePath");
		}
	}

	/// <summary>
	/// And every one accepts <c>path</c> except the single tool that also takes <c>targetPath</c>,
	/// where the word genuinely could mean either file and moving a type into the wrong one is
	/// silent. Asserted as an exact set, so a second tool growing two paths has to be thought about
	/// rather than quietly inheriting the guess.
	/// </summary>
	[Test]
	public void Only_the_tool_with_two_paths_withholds_the_path_alias()
	{
		var (aliases, tools) = Registered();

		var withheld = tools
			.Where(tool => Arguments(tool).Contains("filePath"))
			.Where(tool => !aliases.For(tool.Name).ContainsKey("path"))
			.Select(tool => tool.Name)
			.ToArray();

		withheld.ShouldBe([ToolNames.MoveTypeToFile]);
	}

	/// <summary>
	/// The two lists agree: the tools registered one generic call at a time, and the types read for
	/// aliases. A tool registered from a type nothing scanned looks entirely healthy -- it lists, it
	/// runs, it answers -- and drops any argument a caller spells the other way, which is the silence
	/// the aliases exist to end.
	/// </summary>
	[Test]
	public void Every_tool_the_broker_offers_was_read_for_aliases()
	{
		var (aliases, tools) = Registered();

		tools.ShouldNotBeEmpty();
		tools.Select(tool => tool.Name).Except(aliases.Scanned, StringComparer.Ordinal).ShouldBeEmpty();
	}

	/// <summary>Refused where it is written, not where it is read: registration fails rather than one call.</summary>
	[Test]
	public void An_alias_that_shadows_an_argument_is_refused_at_startup()
	{
		var error = Should.Throw<InvalidOperationException>(() => ArgumentAliases.From([typeof(Shadowing)])).ShouldBeOfType<InvalidOperationException>();

		error.Message.ShouldContain("'path'", Case.Sensitive);
	}

	/// <summary>Two arguments claiming one spelling cannot both be what a caller meant.</summary>
	[Test]
	public void Two_arguments_claiming_one_alias_are_refused_at_startup()
	{
		var error = Should.Throw<InvalidOperationException>(() => ArgumentAliases.From([typeof(Doubled)])).ShouldBeOfType<InvalidOperationException>();

		error.Message.ShouldContain("cannot stand for two", Case.Sensitive);
	}

	/// <summary>The whole point: what the caller sent under the other name is what binds.</summary>
	[Test]
	public void An_alias_is_rewritten_to_the_argument_it_stands_for()
	{
		var aliases = ArgumentAliases.From([typeof(Aliased)]);

		var correction = aliases.Read("aliased", Sent(("path", "D:/repo/App.cs")));

		correction.Refusal.ShouldBeNull();
		correction.Arguments.ShouldNotBeNull();
		correction.Arguments!["filePath"].GetString().ShouldBe("D:/repo/App.cs");
		correction.Arguments.Keys.ShouldNotContain("path");
	}

	/// <summary>
	/// Both spellings, disagreeing, and nothing here knows which was meant. Picking one is the guess
	/// the aliases exist to remove, so the call is refused and both names are said back.
	/// </summary>
	[Test]
	public void A_call_naming_both_is_refused_rather_than_guessed()
	{
		var aliases = ArgumentAliases.From([typeof(Aliased)]);

		var correction = aliases.Read("aliased", Sent(("filePath", "A.cs"), ("path", "B.cs")));

		correction.Arguments.ShouldBeNull();
		correction.Refusal.ShouldNotBeNull();
		correction.Refusal!.ShouldContain("'filePath'", Case.Sensitive);
		correction.Refusal!.ShouldContain("'path'", Case.Sensitive);
	}

	/// <summary>A call that got the name right costs nothing and is handed on untouched.</summary>
	[Test]
	public void A_call_naming_only_the_argument_is_left_alone()
	{
		var aliases = ArgumentAliases.From([typeof(Aliased)]);

		var correction = aliases.Read("aliased", Sent(("filePath", "A.cs")));

		correction.Refusal.ShouldBeNull();
		correction.Arguments.ShouldBeNull();
		correction.Applied.ShouldBeEmpty();
	}

	/// <summary>The aliases the running server applies, and the tools exactly as a client is sent them.</summary>
	private static (ArgumentAliases Aliases, Tool[] Tools) Registered()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		return (
			provider.GetRequiredService<ArgumentAliases>(),
			[.. provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool)]);
	}

	/// <summary>The argument names a tool's schema declares, which is what a client binds against.</summary>
	private static string[] Arguments(Tool tool) =>
		tool.InputSchema.TryGetProperty("properties", out var properties)
			? [.. properties.EnumerateObject().Select(property => property.Name)]
			: [];

	private static Dictionary<string, JsonElement> Sent(params (string Name, string Value)[] arguments) =>
		arguments.ToDictionary(
			argument => argument.Name,
			argument => JsonSerializer.SerializeToElement(argument.Value),
			StringComparer.Ordinal);

	private sealed class Shadowing
	{
		[McpServerTool(Name = "shadowing")]
		public static string Call([ArgumentAlias("path")] string filePath, string path) => filePath + path;
	}

	private sealed class Doubled
	{
		[McpServerTool(Name = "doubled")]
		public static string Call(
			[ArgumentAlias("path")] string filePath,
			[ArgumentAlias("path")] string targetPath) => filePath + targetPath;
	}

	private sealed class Aliased
	{
		[McpServerTool(Name = "aliased")]
		public static string Call([ArgumentAlias("file"), ArgumentAlias("path")] string filePath) => filePath;
	}
}
