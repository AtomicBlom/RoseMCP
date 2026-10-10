using System.Text.Json;

using ModelContextProtocol.Protocol;

using RoseMcp.Server;

namespace RoseMcp.UnitTests;

/// <summary>
/// What decides whether a client hears about a restart: two brokers' tool definitions and
/// instructions compared. Equal must mean nothing a client holds differs, or a deploy that changed a
/// description leaves every session calling with the old one; and a difference in nothing but
/// spelling must not count, or every deploy reloads every session's tool list for nothing.
/// </summary>
public sealed class RelayToolSurfaceTests
{
	[Test]
	public void The_same_definitions_in_another_order_are_the_same_surface()
	{
		var one = ToolSurface.Of(
			[Tool("rose_a", """{"type":"object","properties":{"x":{"type":"string"},"y":{"type":"integer"}}}"""), Tool("rose_b")],
			"Instructions.\r\nMore.");

		var other = ToolSurface.Of(
			[Tool("rose_b"), Tool("rose_a", """{"properties":{"y":{"type":"integer"},"x":{"type":"string"}},"type":"object"}""")],
			"Instructions.\nMore.");

		other.Fingerprint.ShouldBe(one.Fingerprint, "only the order of keys, tools and line endings differ");
		one.ChangesTo(other).IsEmpty.ShouldBeTrue();
	}

	[Test]
	public void A_changed_description_schema_or_annotation_is_a_changed_tool()
	{
		var before = ToolSurface.Of([Tool("rose_a"), Tool("rose_b"), Tool("rose_c")], "Same.");
		var after = ToolSurface.Of(
			[
				Tool("rose_a", description: "Does something else."),
				Tool("rose_b", """{"type":"object","properties":{"z":{"type":"boolean"}}}"""),
				Tool("rose_c", readOnly: true),
			],
			"Same.");

		var change = before.ChangesTo(after);

		after.Fingerprint.ShouldNotBe(before.Fingerprint);
		change.Changed.ShouldBe(["rose_a", "rose_b", "rose_c"]);
		change.Added.ShouldBeEmpty();
		change.Removed.ShouldBeEmpty();
		change.InstructionsChanged.ShouldBeFalse();
	}

	[Test]
	public void Names_what_was_added_and_removed()
	{
		var change = ToolSurface.Of([Tool("rose_a"), Tool("rose_gone")], null)
			.ChangesTo(ToolSurface.Of([Tool("rose_a"), Tool("rose_new")], null));

		change.Added.ShouldBe(["rose_new"]);
		change.Removed.ShouldBe(["rose_gone"]);
		change.Changed.ShouldBeEmpty();
		change.ToolsChanged.ShouldBeTrue();
	}

	/// <summary>Instructions are part of what a client holds, so a change to them alone is a change.</summary>
	[Test]
	public void Changed_instructions_change_the_surface_and_nothing_else()
	{
		var before = ToolSurface.Of([Tool("rose_a")], "Use rose.");
		var after = ToolSurface.Of([Tool("rose_a")], "Use rose, carefully.");

		after.Fingerprint.ShouldNotBe(before.Fingerprint);

		var change = before.ChangesTo(after);
		change.InstructionsChanged.ShouldBeTrue();
		change.ToolsChanged.ShouldBeFalse();
		change.IsEmpty.ShouldBeFalse();

		before.WithInstructions("Use rose, carefully.").ChangesTo(after).IsEmpty.ShouldBeTrue();
	}

	/// <summary>
	/// The SDK's own metadata is left out: it says nothing a model reads, and a broker that varied it
	/// would otherwise send every session a refresh on every deploy.
	/// </summary>
	[Test]
	public void Metadata_is_not_part_of_the_comparison()
	{
		var plain = Tool("rose_a");
		var tagged = Tool("rose_a");
		tagged.Meta = new System.Text.Json.Nodes.JsonObject { ["build"] = "abc" };

		ToolSurface.Of([tagged], null).Fingerprint.ShouldBe(ToolSurface.Of([plain], null).Fingerprint);
	}

	[Test]
	public void Says_which_tools_it_declares()
	{
		var surface = ToolSurface.Of([Tool("rose_debug_list")], null);

		surface.Declares("rose_debug_list").ShouldBeTrue();
		surface.Declares("rose_outline").ShouldBeFalse();
	}

	private static Tool Tool(
		string name,
		string schema = """{"type":"object","properties":{}}""",
		string description = "Does something.",
		bool readOnly = false) => new()
		{
			Name = name,
			Description = description,
			InputSchema = JsonDocument.Parse(schema).RootElement.Clone(),
			Annotations = readOnly ? new ToolAnnotations { ReadOnlyHint = true } : null,
		};
}
