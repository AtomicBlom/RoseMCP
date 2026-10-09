using System.ComponentModel;
using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.TestSupport;

namespace RoseMcp.UnitTests;

/// <summary>
/// The worker's own boundary, driven over a real MCP connection in this process. The classifier and the
/// status fold are tested on their own; this is the half that joins them, and if the filter stops seeing the
/// exception or stops reaching the host, the caller is back to a loader message about a file and a workspace
/// that calls itself healthy -- with every other test still passing.
/// </summary>
public sealed class WorkerToolErrorTests
{
	private const string Solution = @"D:\repo\A.slnx";

	private const string Compression =
		"System.IO.Compression, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

	[Test]
	public async Task An_assembly_the_worker_cannot_load_is_explained_and_recorded(CancellationToken cancellationToken)
	{
		await using var host = Host();
		await using var connection = await Connection.OpenAsync(host, cancellationToken);

		var result = await connection.Client.CallToolAsync(
			"rose_find_references",
			new Dictionary<string, object?> { ["symbol"] = "A.B" },
			cancellationToken: cancellationToken);

		result.IsError.ShouldBe(true);
		var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
		text.ShouldContain("rose_find_references could not run, because this worker could not load System.IO.Compression", Case.Sensitive);
		text.ShouldContain("rose_workspace_reload starts a fresh worker", Case.Sensitive);
		text.ShouldContain($"(workspace: {Solution})", Case.Sensitive);

		var fault = host.AssemblyLoadFaults.ShouldHaveSingleItem();
		fault.Assembly.ShouldBe("System.IO.Compression");
		fault.Tool.ShouldBe("rose_find_references");
	}

	/// <summary>A file the caller named not being there is forwarded as it was, and the worker is not marked hurt.</summary>
	[Test]
	public async Task A_missing_source_file_is_forwarded_and_not_recorded(CancellationToken cancellationToken)
	{
		await using var host = Host();
		await using var connection = await Connection.OpenAsync(host, cancellationToken);

		var result = await connection.Client.CallToolAsync(
			"rose_outline",
			new Dictionary<string, object?> { ["filePath"] = @"D:\repo\Widget.cs" },
			cancellationToken: cancellationToken);

		result.IsError.ShouldBe(true);
		var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
		text.ShouldContain(@"Could not find file 'D:\repo\Widget.cs'.", Case.Sensitive);
		text.ShouldNotContain("rose_workspace_reload", Case.Sensitive);
		host.AssemblyLoadFaults.ShouldBeEmpty();
	}

	/// <summary>Never started: the boundary records on the host, which needs no loaded solution to hold a fault.</summary>
	private static WorkspaceHost Host() => new(
		new WorkerOptions { SolutionPath = Solution },
		new SolutionLoader(
			new RestoreRunner(NullLogger<RestoreRunner>.Instance),
			new ShadowCopyAnalyzerAssemblyLoader(NullLogger<ShadowCopyAnalyzerAssemblyLoader>.Instance),
			NullLogger<SolutionLoader>.Instance),
		new SharedWorkProgress(),
		NullLoggerFactory.Instance,
		new NeverStops(),
		NullLogger<WorkspaceHost>.Instance);

	/// <summary>Two tools that fail the way the worker's do.</summary>
	public sealed class Tools
	{
		[McpServerTool(Name = "rose_find_references")]
		[Description("Fails as Roslyn's symbol index did when the runtime could not load a framework assembly.")]
		public static string FindReferences(string symbol) =>
			throw new FileNotFoundException(
				$"Could not load file or assembly '{Compression}'. The system cannot find the file specified.",
				Compression);

		[McpServerTool(Name = "rose_outline")]
		[Description("Fails as a read of a file that is not there does.")]
		public static string Outline(string filePath) =>
			throw new FileNotFoundException($"Could not find file '{filePath}'.", filePath);
	}

	/// <summary>The test tools behind the worker's boundary filter, with the host registered as the worker registers it.</summary>
	private sealed class Connection : IAsyncDisposable
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

		public static async Task<Connection> OpenAsync(WorkspaceHost host, CancellationToken cancellationToken)
		{
			var toServer = new Pipe();
			var toClient = new Pipe();

			var services = new ServiceCollection();
			services.AddLogging();
			services.AddSingleton(host);
			ToolErrorReporting.WithToolErrorMessages(services.AddMcpServer().WithTools<Tools>(), Solution)
				.WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());

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
