using System.ComponentModel;
using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.LiveApp;

namespace RoseMcp.IntegrationTests.Windows;

/// <summary>
/// No refusal at the live-app host's boundary carries a CLR parameter name, whoever threw it. The broker's
/// and the worker's boundaries are held the same way in the unit suite; this host is
/// <c>net10.0-windows</c>, so only this project can drive its filter.
/// </summary>
public sealed class ParameterNameBoundaryTests
{
	[Test]
	[Arguments("rose_line")]
	[Arguments("rose_spec")]
	[Arguments("rose_index")]
	[Arguments("rose_required")]
	public async Task No_refusal_carries_a_parameter_name(string tool, CancellationToken cancellationToken)
	{
		var text = await RefusalAsync(tool, cancellationToken);

		text.ShouldNotContain("(Parameter '", Case.Sensitive);
	}

	/// <summary>A refusal this host wrote keeps its words, with a parameter the tool declares said as its argument.</summary>
	[Test]
	public async Task A_refusal_naming_a_declared_argument_says_it_in_the_tools_terms(CancellationToken cancellationToken)
	{
		var text = await RefusalAsync("rose_line", cancellationToken);

		text.ShouldContain("A.cs has 3 line(s); line 9 does not exist. (argument `line`)", Case.Sensitive);
		text.ShouldNotContain("failed inside", Case.Sensitive);
	}

	/// <summary>An exception that escaped the BCL is framed as the fault it is, with its words kept.</summary>
	[Test]
	public async Task A_leaked_framework_exception_is_framed_as_a_fault(CancellationToken cancellationToken)
	{
		var text = await RefusalAsync("rose_index", cancellationToken);

		text.ShouldContain("`rose_index` failed inside .NET rather than refusing the call.", Case.Sensitive);
	}

	private static async Task<string> RefusalAsync(string tool, CancellationToken cancellationToken)
	{
		var toServer = new Pipe();
		var toClient = new Pipe();

		var services = new ServiceCollection();
		services.AddLogging();
		services.AddMcpServer()
			.WithTools<Tools>()
			.WithToolErrorMessages()
			.WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());

		await using var provider = services.BuildServiceProvider();
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var running = provider.GetRequiredService<McpServer>().RunAsync(stop.Token);

		try
		{
			await using var client = await McpClient.CreateAsync(
				new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
				cancellationToken: cancellationToken);

			var arguments = tool switch
			{
				"rose_line" => new Dictionary<string, object?> { ["filePath"] = "A.cs", ["line"] = 9 },
				"rose_spec" => new Dictionary<string, object?> { ["location"] = "nowhere" },
				"rose_index" => new Dictionary<string, object?> { ["session"] = "s1" },
				_ => new Dictionary<string, object?>(),
			};

			var result = await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);

			result.IsError.ShouldBe(true);

			return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
		}
		finally
		{
			await stop.CancelAsync();

			try
			{
				await running;
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	/// <summary>Tools that fail each way a tool can: a refusal this host wrote, and an exception that escaped a framework.</summary>
	[McpServerToolType]
	public sealed class Tools
	{
		[McpServerTool(Name = "rose_line")]
		[Description("Refuses a line past the end, naming the argument.")]
		public static string Line(string filePath, int line) =>
			throw new ArgumentOutOfRangeException(nameof(line), $"{filePath} has 3 line(s); line {line} does not exist.");

		[McpServerTool(Name = "rose_spec")]
		[Description("Refuses through a parser whose parameter is not one of the tool's arguments, as a location is parsed.")]
		public static string Spec(string location) =>
			throw new ArgumentException($"Expected Namespace.Type.Method, got '{location}'.", "spec");

		[McpServerTool(Name = "rose_index")]
		[Description("Indexes past the end of a list, which the BCL refuses by throwing.")]
		public static string Index(string session) => new List<string> { session }[3];

		[McpServerTool(Name = "rose_required")]
		[Description("Takes one argument it cannot do without.")]
		public static string Required(string session) => session;
	}
}
