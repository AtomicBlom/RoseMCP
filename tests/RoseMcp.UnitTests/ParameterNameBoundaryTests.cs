using System.ComponentModel;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// No refusal at the broker's or the worker's boundary carries a CLR parameter name, whoever threw it.
/// <para>
/// The caller's vocabulary is the tool's schema, and a <c>(Parameter 'symbol')</c> from Roslyn reads as
/// advice about an argument -- one the tool may not have, or the one argument the caller got right. Driven
/// over a real MCP connection through each host's own filter, because the rule is a property of the
/// boundary and a filter that stopped running it would leave every unit test of the rewrite passing. The
/// live-app host's boundary is held the same way in <c>RoseMcp.IntegrationTests.Windows</c>, the only
/// project that can reference it.
/// </para>
/// </summary>
public sealed class ParameterNameBoundaryTests
{
	private const string Solution = @"D:\repo\A.slnx";

	/// <summary>
	/// The worker's own words for a relayed refusal, as the broker receives them: already framed, with a
	/// workspace after the message.
	/// </summary>
	private const string Relayed =
		"`rose_resolve_name` failed inside Roslyn rather than refusing the call. Roslyn said: Parameter 'symbol' "
		+ "must be a symbol from this compilation or some referenced assembly. Any parameter it names is Roslyn's own, "
		+ "not an argument of `rose_resolve_name`; if the arguments sent are right, this is a fault in Rose worth "
		+ "reporting. (workspace: D:\\repo\\A.slnx)";

	public static IEnumerable<Func<(string Boundary, string Tool, Dictionary<string, object?> Arguments)>> Calls()
	{
		foreach (var boundary in new[] { "broker", "worker" })
		{
			yield return () => (boundary, "rose_line", new() { ["filePath"] = "A.cs", ["line"] = 9 });
			yield return () => (boundary, "rose_spec", new() { ["location"] = "nowhere" });
			yield return () => (boundary, "rose_resolve_name", new() { ["name"] = "A" });
			yield return () => (boundary, "rose_index", new() { ["symbol"] = "A" });
			yield return () => (boundary, "rose_required", new());
		}
	}

	[Test]
	[MethodDataSource(nameof(Calls))]
	public async Task No_refusal_carries_a_parameter_name(
		(string Boundary, string Tool, Dictionary<string, object?> Arguments) call,
		CancellationToken cancellationToken)
	{
		var text = await RefusalAsync(call.Boundary, call.Tool, call.Arguments, cancellationToken);

		text.ShouldNotContain("(Parameter '", Case.Sensitive);
	}

	/// <summary>A parameter name the tool declares is said as its argument; Rose wrote that refusal, so it is not framed as a fault.</summary>
	[Test]
	[Arguments("broker")]
	[Arguments("worker")]
	public async Task A_refusal_naming_a_declared_argument_says_it_in_the_tools_terms(string boundary, CancellationToken cancellationToken)
	{
		var text = await RefusalAsync(boundary, "rose_line", new() { ["filePath"] = "A.cs", ["line"] = 9 }, cancellationToken);

		text.ShouldStartWith("A.cs has 3 line(s); line 9 does not exist. (argument `line`)", Case.Sensitive);
		text.ShouldNotContain("failed inside", Case.Sensitive);
	}

	/// <summary>The exception that made this urgent, at the boundary: framed as Roslyn's, with its words kept.</summary>
	[Test]
	[Arguments("broker")]
	[Arguments("worker")]
	public async Task A_leaked_Roslyn_exception_is_framed_as_a_fault(string boundary, CancellationToken cancellationToken)
	{
		var text = await RefusalAsync(boundary, "rose_resolve_name", new() { ["name"] = "A" }, cancellationToken);

		text.ShouldStartWith("`rose_resolve_name` failed inside Roslyn rather than refusing the call.", Case.Sensitive);
		text.ShouldContain("must be a symbol from this compilation", Case.Sensitive);
	}

	/// <summary>
	/// A required argument left out is the binder refusing what the caller sent, which is a refusal and not
	/// a fault, though the binder is not Rose's code.
	/// </summary>
	[Test]
	[Arguments("broker")]
	[Arguments("worker")]
	public async Task A_missing_argument_is_a_refusal_rather_than_a_fault(string boundary, CancellationToken cancellationToken)
	{
		var text = await RefusalAsync(boundary, "rose_required", new(), cancellationToken);

		text.ShouldContain("symbol", Case.Sensitive);
		text.ShouldNotContain("failed inside", Case.Sensitive);
	}

	/// <summary>
	/// A worker's refusal reaches the broker as the broker's own exception, already in the caller's terms,
	/// and the broker's pass over it leaves it word for word: framed once, never twice.
	/// </summary>
	[Test]
	public async Task The_broker_leaves_a_relayed_refusal_as_the_worker_wrote_it(CancellationToken cancellationToken)
	{
		var text = await RefusalAsync("broker", "rose_relayed", new() { ["symbol"] = "A" }, cancellationToken);

		text.ShouldBe(Relayed);
	}

	private static async Task<string> RefusalAsync(
		string boundary,
		string tool,
		Dictionary<string, object?> arguments,
		CancellationToken cancellationToken)
	{
		await using var connection = await IgnoredArgumentsTests.Connection.OpenAsync(
			services => boundary == "broker"
				? RoseMcp.Broker.ToolErrorReporting.WithToolErrorMessages(services.AddMcpServer().WithTools<Tools>())
				: RoseMcp.Worker.ToolErrorReporting.WithToolErrorMessages(services.AddMcpServer().WithTools<Tools>(), Solution),
			cancellationToken);

		var result = await connection.Client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);

		result.IsError.ShouldBe(true);

		// Read as the broker reads a far side's failure, past the wrapper the SDK puts round it on the wire.
		var text = ForwardedError.Message(result).ShouldNotBeNull();

		// The worker names its workspace after every message; what comes before it is what is under test.
		var workspace = $" (workspace: {Solution})";
		var isWorkerSuffixed = boundary == "worker" && text.EndsWith(workspace, StringComparison.Ordinal);

		return isWorkerSuffixed ? text[..^workspace.Length] : text;
	}

	/// <summary>Tools that fail each way a tool can: a refusal Rose wrote, and an exception that escaped a framework.</summary>
	[McpServerToolType]
	public sealed class Tools
	{
		[McpServerTool(Name = "rose_line")]
		[Description("Refuses a line past the end, naming the argument as the worker's locator does.")]
		public static string Line(string filePath, int line) =>
			throw new ArgumentOutOfRangeException(nameof(line), $"{filePath} has 3 line(s); line {line} does not exist.");

		[McpServerTool(Name = "rose_spec")]
		[Description("Refuses through a parser whose parameter is not one of the tool's arguments.")]
		public static string Spec(string location) =>
			throw new ArgumentException($"Expected Namespace.Type.Method, got '{location}'.", "spec");

		[McpServerTool(Name = "rose_resolve_name")]
		[Description("Asks one compilation about another's symbol, which Roslyn refuses by throwing.")]
		public static string ResolveName(string name)
		{
			var asking = CSharpCompilation.Create("Asking", [CSharpSyntaxTree.ParseText($"class {name} {{ }}")]);
			var other = CSharpCompilation.Create("Other", [CSharpSyntaxTree.ParseText("class B { }")]);

			return asking.IsSymbolAccessibleWithin(other.GetTypeByMetadataName("B")!, asking.Assembly).ToString();
		}

		[McpServerTool(Name = "rose_index")]
		[Description("Indexes past the end of a list, which the BCL refuses by throwing.")]
		public static string Index(string symbol) => new List<string> { symbol }[3];

		[McpServerTool(Name = "rose_required")]
		[Description("Takes one argument it cannot do without.")]
		public static string Required(string symbol) => symbol;

		[McpServerTool(Name = "rose_relayed")]
		[Description("Fails as the broker does when a worker refused the call it relayed.")]
		public static string RelayedRefusal(string symbol) => throw new InvalidOperationException(Relayed);
	}
}
