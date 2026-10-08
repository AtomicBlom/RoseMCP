using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The text block a typed tool's answer is mirrored into, which is what most clients hand the model.
/// The SDK writes it with the framework's default encoder unless told otherwise, and that encoder
/// spells every <c>+</c> in a diff as a six-character escape that no client decodes.
/// </summary>
public sealed class ToolTextTests
{
	/// <summary>Every character the default encoder escapes and JSON does not need escaped, plus a quote.</summary>
	private const string Awkward = """a + b < c > d " e ' f ` g & h""";

	private const string Notice = "Ignored an argument called `y`; `rose_echo` has no such argument. It takes `said`.";

	/// <summary>
	/// How the default encoder spells each of those characters, built from a backslash and its code
	/// so no tool that writes this file can decode them. Named one by one rather than as any
	/// backslash followed by a u, which a Windows path in an answer can hold legitimately.
	/// </summary>
	private static readonly string[] Escapes =
		[.. new[] { "002B", "003C", "003E", "0022", "0027", "0060", "0026" }.Select(code => "\\" + "u" + code)];

	/// <summary>
	/// Through the SDK's own serialization, registered the way every host registers its tools: what
	/// the tool said reads back as itself, not as escapes.
	/// </summary>
	[Test]
	public async Task A_result_text_spells_every_printable_character_as_itself(CancellationToken cancellationToken)
	{
		await using var connection = await OpenAsync(cancellationToken);

		var result = await connection.Client.CallToolAsync(
			"rose_echo",
			new Dictionary<string, object?> { ["said"] = Awkward },
			cancellationToken: cancellationToken);

		var text = result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
		text.ShouldBe("""{"said":"a + b < c > d \" e ' f ` g & h"}""");
		Escapes.ShouldNotContain(escape => text.Contains(escape, StringComparison.Ordinal));
		JsonDocument.Parse(text).RootElement.GetProperty("said").GetString().ShouldBe(Awkward);
	}

	/// <summary>
	/// A call that carried an argument the tool does not declare has its text block rewritten to add
	/// the notice. It is the same text the SDK wrote for the plain call, field for field and character
	/// for character, with the notices on the end: one spelling for one answer, whichever way it came.
	/// </summary>
	[Test]
	public async Task A_rewritten_result_is_spelled_as_the_SDK_spelled_the_plain_one(CancellationToken cancellationToken)
	{
		await using var connection = await OpenAsync(cancellationToken);

		var plain = await connection.Client.CallToolAsync(
			"rose_echo",
			new Dictionary<string, object?> { ["said"] = Awkward },
			cancellationToken: cancellationToken);
		var noticed = await connection.Client.CallToolAsync(
			"rose_echo",
			new Dictionary<string, object?> { ["said"] = Awkward, ["y"] = 1 },
			cancellationToken: cancellationToken);

		var plainText = plain.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
		var noticedText = noticed.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
		noticedText.ShouldBe(plainText[..^1] + ",\"notices\":[\"" + Notice + "\"]}");
	}

	/// <summary>
	/// The broker as <c>AddRoseMcpBroker</c> registers it, answering about a solution whose path holds
	/// a plus, an apostrophe, a backtick and an ampersand, with and without an ignored argument. The
	/// path comes back as written both times, and the two answers differ only by the notice, so the
	/// workspace attribution survives the rewrite in place and in order.
	/// </summary>
	[Test]
	public async Task The_broker_spells_a_result_one_way_whichever_path_wrote_it(CancellationToken cancellationToken)
	{
		var directory = Directory.CreateTempSubdirectory("rose-a+b'c`d&e-");
		try
		{
			var solution = Path.Combine(directory.FullName, "A.slnx");
			await File.WriteAllTextAsync(solution, "<Solution />", cancellationToken);

			await using var connection = await IgnoredArgumentsTests.Connection.OpenAsync(
				services => services.AddRoseMcpBroker(),
				cancellationToken);

			var plain = await connection.Client.CallToolAsync(
				"rose_workspace_close",
				new Dictionary<string, object?> { ["workspace"] = solution },
				cancellationToken: cancellationToken);
			var noticed = await connection.Client.CallToolAsync(
				"rose_workspace_close",
				new Dictionary<string, object?> { ["workspace"] = solution, ["worksapce"] = solution },
				cancellationToken: cancellationToken);

			var plainText = plain.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
			plainText.ShouldContain("rose-a+b'c`d&e-", Case.Sensitive);
			Escapes.ShouldNotContain(escape => plainText.Contains(escape, StringComparison.Ordinal));

			var notice = "Ignored an argument called `worksapce`; `rose_workspace_close` has no such argument. Did you mean `workspace`?";
			var noticedText = noticed.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
			noticedText.ShouldBe(plainText[..^1] + ",\"notices\":[\"" + notice + "\"]}");
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	/// <summary>
	/// One instance for every host and filter that asks, differing from the SDK's options in the
	/// encoder and nothing else, so a result changes spelling and never shape.
	/// </summary>
	[Test]
	public void The_options_are_the_SDK_s_own_with_one_encoder()
	{
		var basis = McpJsonUtilities.DefaultOptions;
		var options = ToolJson.Readable(basis);

		ToolJson.Readable(basis).ShouldBeSameAs(options);
		options.IsReadOnly.ShouldBeTrue();
		options.Encoder.ShouldBeSameAs(ToolJson.Encoder);
		options.PropertyNamingPolicy.ShouldBeSameAs(basis.PropertyNamingPolicy);
		options.DefaultIgnoreCondition.ShouldBe(basis.DefaultIgnoreCondition);
		options.NumberHandling.ShouldBe(basis.NumberHandling);
		options.TypeInfoResolver.ShouldBeSameAs(basis.TypeInfoResolver);
	}

	private static Task<IgnoredArgumentsTests.Connection> OpenAsync(CancellationToken cancellationToken) =>
		IgnoredArgumentsTests.Connection.OpenAsync(
			services => services.AddMcpServer()
				.WithTools<Tools>(ToolJson.Readable(McpJsonUtilities.DefaultOptions))
				.WithIgnoredArgumentNotices(),
			cancellationToken);

	/// <summary>A tool that says back what it was given, as structured content.</summary>
	[McpServerToolType]
	public sealed class Tools
	{
		[McpServerTool(Name = "rose_echo", UseStructuredContent = true)]
		[Description("Says it back.")]
		public static Echoed Echo(string said) => new(said);
	}

	/// <summary>What the test tool answers.</summary>
	public sealed record Echoed(string Said);
}
