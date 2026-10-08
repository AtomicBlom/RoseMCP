using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// A call that succeeds while the binder drops one of its arguments. Without a notice,
/// <c>rose_find_references(symbol: "X", projet: "A")</c> searches every project and answers a
/// question nobody asked, with nothing in the answer saying the project was set aside.
/// </summary>
public sealed class IgnoredArgumentsTests
{
	private const string Notice = "Ignored an argument called `path`; `rose_x` has no such argument. Did you mean `filePath`?";

	/// <summary>
	/// A result that already says things in <c>notices</c> says this there too, and the text block
	/// that mirrors it says the same, so a client reading either sees one answer.
	/// </summary>
	[Test]
	public void Joins_the_notices_a_result_already_has()
	{
		var result = Structured("""{"revision":3,"notices":["Absorbed 1 external file change(s)."]}""");

		IgnoredArguments.Noticed(result, [Notice]);

		Notices(result.StructuredContent!.Value).ShouldBe(["Absorbed 1 external file change(s).", Notice]);
		var text = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
		Notices(JsonDocument.Parse(text).RootElement).ShouldBe(["Absorbed 1 external file change(s).", Notice]);
		text.ShouldContain("`path`", Case.Sensitive);
	}

	/// <summary>
	/// A result type with no notices of its own gains the field under the same name, rather than a
	/// second field only these results use.
	/// </summary>
	[Test]
	public void Gives_a_result_without_notices_the_same_field()
	{
		var result = Structured("""{"revision":3,"references":[]}""");

		IgnoredArguments.Noticed(result, [Notice]);

		var structured = result.StructuredContent!.Value;
		structured.GetProperty("revision").GetInt32().ShouldBe(3);
		Notices(structured).ShouldBe([Notice]);
		Notices(JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement).ShouldBe([Notice]);
	}

	/// <summary>A tool answering in prose gets the notice as a block of prose after its answer.</summary>
	[Test]
	public void Adds_a_text_block_to_a_result_in_plain_text()
	{
		var result = new CallToolResult { Content = [new TextContentBlock { Text = "Detached." }] };

		IgnoredArguments.Noticed(result, [Notice]);

		result.Content.OfType<TextContentBlock>().Select(block => block.Text).ShouldBe(["Detached.", Notice]);
		result.StructuredContent.ShouldBeNull();
	}

	/// <summary>
	/// A <c>notices</c> field of some other shape is not the caller's list to append to, so the
	/// structured content is left alone and the notice still reaches the caller as text.
	/// </summary>
	[Test]
	public void Leaves_a_notices_field_of_another_shape_alone()
	{
		var result = Structured("""{"notices":"none"}""");

		IgnoredArguments.Noticed(result, [Notice]);

		result.StructuredContent!.Value.GetProperty("notices").GetString().ShouldBe("none");
		result.Content.OfType<TextContentBlock>().Last().Text.ShouldBe(Notice);
	}

	[Test]
	public void Changes_nothing_where_there_is_nothing_to_say()
	{
		var result = Structured("""{"revision":3}""");
		var before = result.Content.Single();

		IgnoredArguments.Noticed(result, []);

		result.Content.Single().ShouldBeSameAs(before);
		result.StructuredContent!.Value.TryGetProperty("notices", out _).ShouldBeFalse();
	}

	/// <summary>
	/// The whole path, through a real server and the SDK's own binder: the misspelled argument is
	/// dropped, the tool runs on its default, and the answer says which argument it never saw.
	/// </summary>
	[Test]
	public async Task A_call_that_ran_without_its_argument_says_so(CancellationToken cancellationToken)
	{
		await using var connection = await Connection.OpenAsync(cancellationToken);

		var result = await connection.Client.CallToolAsync(
			"rose_x",
			new Dictionary<string, object?> { ["symbol"] = "A", ["path"] = "src/A.cs" },
			cancellationToken: cancellationToken);

		result.IsError.ShouldNotBe(true);
		var structured = result.StructuredContent!.Value;
		var boundFilePath = structured.TryGetProperty("filePath", out var filePath) && filePath.ValueKind != JsonValueKind.Null;
		boundFilePath.ShouldBeFalse();
		Notices(structured).ShouldBe([Notice]);

		// The SDK's own text block, rewritten rather than joined by a second one.
		var text = result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
		Notices(JsonDocument.Parse(text).RootElement).ShouldBe([Notice]);
	}

	/// <summary>
	/// A call naming only declared arguments is answered exactly as the tool wrote it, so a result
	/// type without notices does not grow an empty list on every ordinary call.
	/// </summary>
	[Test]
	public async Task A_call_with_only_declared_arguments_is_left_as_it_was(CancellationToken cancellationToken)
	{
		await using var connection = await Connection.OpenAsync(cancellationToken);

		var result = await connection.Client.CallToolAsync(
			"rose_x",
			new Dictionary<string, object?> { ["symbol"] = "A", ["filePath"] = "src/A.cs" },
			cancellationToken: cancellationToken);

		result.StructuredContent!.Value.TryGetProperty("notices", out _).ShouldBeFalse();
	}

	/// <summary>
	/// The refusal half, through the same server: the tool reports a value missing that the caller
	/// sent under the wrong name, and the boundary says which name.
	/// </summary>
	[Test]
	public async Task A_refusal_names_the_argument_it_never_saw(CancellationToken cancellationToken)
	{
		await using var connection = await Connection.OpenAsync(cancellationToken);

		var result = await connection.Client.CallToolAsync(
			"rose_x",
			new Dictionary<string, object?> { ["path"] = "src/A.cs" },
			cancellationToken: cancellationToken);

		result.IsError.ShouldBe(true);
		var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
		text.ShouldContain(
			"Name a type, or give a file path. `path` is not an argument of `rose_x`. Did you mean `filePath`?",
			Case.Sensitive);
	}

	/// <summary>
	/// The broker's own chain, as <c>AddRoseMcpBroker</c> registers it. <c>solution</c> is a spelling
	/// the alias filter accepts for <c>workspace</c>, and is rewritten before anything reads the
	/// arguments, so it is never called ignored; <c>worksapce</c> is accepted by nothing, so it is.
	/// <c>rose_workspace_close</c> because it answers without a worker: no worker is open for the
	/// solution, so closing it is a well-formed "nothing was open".
	/// </summary>
	[Test]
	public async Task The_broker_notices_a_misspelling_and_not_a_spelling_it_accepts(CancellationToken cancellationToken)
	{
		var directory = Directory.CreateTempSubdirectory("rose-ignored-");
		try
		{
			var solution = Path.Combine(directory.FullName, "A.slnx");
			await File.WriteAllTextAsync(solution, "<Solution />", cancellationToken);

			await using var connection = await Connection.OpenAsync(services => services.AddRoseMcpBroker(), cancellationToken);

			var aliased = await connection.Client.CallToolAsync(
				"rose_workspace_close",
				new Dictionary<string, object?> { ["solution"] = solution },
				cancellationToken: cancellationToken);

			aliased.IsError.ShouldNotBe(true);
			aliased.StructuredContent!.Value.TryGetProperty("notices", out _).ShouldBeFalse();

			var misspelled = await connection.Client.CallToolAsync(
				"rose_workspace_close",
				new Dictionary<string, object?> { ["workspace"] = solution, ["worksapce"] = solution },
				cancellationToken: cancellationToken);

			misspelled.IsError.ShouldNotBe(true);
			Notices(misspelled.StructuredContent!.Value).ShouldBe([
				"Ignored an argument called `worksapce`; `rose_workspace_close` has no such argument. Did you mean `workspace`?",
			]);
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	/// <summary>
	/// The refusal half of the same chain: a call the broker refuses names <c>projet</c>, which
	/// nothing accepts, and says nothing about <c>path</c>, which the alias filter took as
	/// <c>filePath</c> before the error boundary read the arguments.
	/// </summary>
	[Test]
	public async Task The_broker_names_a_misspelling_in_a_refusal_and_not_a_spelling_it_accepts(CancellationToken cancellationToken)
	{
		var directory = Directory.CreateTempSubdirectory("rose-ignored-");
		try
		{
			var missing = Path.Combine(directory.FullName, "Missing.slnx");

			await using var connection = await Connection.OpenAsync(services => services.AddRoseMcpBroker(), cancellationToken);

			var refused = await connection.Client.CallToolAsync(
				"rose_find_references",
				new Dictionary<string, object?>
				{
					["symbol"] = "A.B",
					["path"] = Path.Combine(directory.FullName, "A.cs"),
					["projet"] = "A",
					["workspace"] = missing,
				},
				cancellationToken: cancellationToken);

			refused.IsError.ShouldBe(true);
			var text = string.Join(" ", refused.Content.OfType<TextContentBlock>().Select(block => block.Text));
			text.ShouldContain("`projet` is not an argument of `rose_find_references`. Did you mean `project`?", Case.Sensitive);
			text.ShouldNotContain("`path`", Case.Sensitive);
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	private static CallToolResult Structured(string json)
	{
		var element = JsonDocument.Parse(json).RootElement;

		return new CallToolResult
		{
			StructuredContent = element,
			Content = [new TextContentBlock { Text = element.GetRawText() }],
		};
	}

	private static IReadOnlyList<string?> Notices(JsonElement structured) =>
		[.. structured.GetProperty("notices").EnumerateArray().Select(notice => notice.GetString())];

	/// <summary>
	/// A tool with the shape that went wrong: two arguments, either of which will do, and a result
	/// type that has no notices of its own.
	/// </summary>
	[McpServerToolType]
	public sealed class Tools
	{
		[McpServerTool(Name = "rose_x", UseStructuredContent = true)]
		[Description("Finds a thing.")]
		public static Found Find(string? symbol = null, string? filePath = null) =>
			symbol is null && filePath is null
				? throw new ArgumentException("Name a type, or give a file path.")
				: new Found(symbol, filePath);
	}

	/// <summary>What the test tool answers.</summary>
	public sealed record Found(string? Symbol, string? FilePath);

	/// <summary>
	/// A server and a client joined by pipes in this process, with the broker's two boundary filters
	/// in the order the broker registers them.
	/// </summary>
	internal sealed class Connection : IAsyncDisposable
	{
		private readonly ServiceProvider _services;
		private readonly CancellationTokenSource _stop;
		private readonly Task _running;

		private Connection(ServiceProvider services, CancellationTokenSource stop, Task running, McpClient client)
		{
			_services = services;
			_stop = stop;
			_running = running;
			Client = client;
		}

		public McpClient Client { get; }

		/// <summary>The test tool, behind the broker's two boundary filters in the order the broker registers them.</summary>
		public static Task<Connection> OpenAsync(CancellationToken cancellationToken) =>
			OpenAsync(
				services => services.AddMcpServer()
					.WithTools<Tools>()
					.WithIgnoredArgumentNotices()
					.WithToolErrorMessages(),
				cancellationToken);

		/// <summary>Whatever server the caller registers, reached over pipes.</summary>
		public static async Task<Connection> OpenAsync(
			Func<IServiceCollection, IMcpServerBuilder> server,
			CancellationToken cancellationToken)
		{
			var toServer = new Pipe();
			var toClient = new Pipe();

			var services = new ServiceCollection();
			services.AddLogging();
			server(services).WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());

			var provider = services.BuildServiceProvider();
			var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var running = provider.GetRequiredService<McpServer>().RunAsync(stop.Token);

			var client = await McpClient.CreateAsync(
				new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
				cancellationToken: cancellationToken);

			return new Connection(provider, stop, running, client);
		}

		public async ValueTask DisposeAsync()
		{
			await Client.DisposeAsync();
			await _stop.CancelAsync();

			try
			{
				await _running;
			}
			catch (OperationCanceledException)
			{
			}

			_stop.Dispose();
			await _services.DisposeAsync();
		}
	}
}
