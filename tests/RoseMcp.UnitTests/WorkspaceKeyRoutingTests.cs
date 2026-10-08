using System.Reflection;
using System.Runtime.ExceptionServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Broker.Tools;
using RoseMcp.Solutions;
using RoseMcp.TestSupport;

namespace RoseMcp.UnitTests;

/// <summary>
/// The key every result carries, sent back as <c>workspaceKey</c> to name the workspace that
/// produced it.
/// <para>
/// A key is a hash of a path, so it is looked up among the loaded workers rather than resolved, and
/// everything worth asserting about that lookup is a refusal: the key nobody carries, the key two
/// workspaces share, and the call naming a workspace twice. None of those needs a worker, so all of
/// them are here; a key that resolves to a real worker is covered by the routing suite.
/// </para>
/// </summary>
public sealed class WorkspaceKeyRoutingTests
{
	private static readonly string Alpha = Path.Combine(NowhereDirectory.Path(), "alpha", "Shop.slnx");
	private static readonly string Beta = Path.Combine(NowhereDirectory.Path(), "beta", "Shop.slnx");

	/// <summary>The ordinary case: the key a result carried names the solution that produced it.</summary>
	[Test]
	public void A_key_names_the_loaded_solution_it_was_derived_from()
	{
		WorkspaceManager.ByKey(WorkspaceKey.For(Beta), [Alpha, Beta]).ShouldBe(Beta);
	}

	/// <summary>
	/// Two worktrees of one repository hold solutions of one name, which is why the key is hashed: the
	/// readable half alone matches both, and only the hash tells them apart.
	/// </summary>
	[Test]
	public void A_key_tells_apart_solutions_of_one_name()
	{
		WorkspaceManager.ByKey(WorkspaceKey.For(Alpha), [Alpha, Beta]).ShouldBe(Alpha);
	}

	/// <summary>An agent echoing a key may change its case or pad it, and still means the same workspace.</summary>
	[Test]
	public void A_key_is_matched_without_regard_to_case_or_padding()
	{
		var echoed = $"  {WorkspaceKey.For(Beta).ToUpperInvariant()} ";

		WorkspaceManager.ByKey(echoed, [Alpha, Beta]).ShouldBe(Beta);
	}

	/// <summary>
	/// A key nothing loaded carries, as after a broker restart, is refused with the keys that would
	/// work and the argument that works regardless, rather than passed over for a guess.
	/// </summary>
	[Test]
	public void An_unknown_key_names_the_loaded_keys_and_the_workspace_argument()
	{
		var error = Should.Throw<McpException>(() => WorkspaceManager.ByKey("Shop-00000000", [Alpha, Beta]))
			.ShouldBeOfType<McpException>();

		error.Message.ShouldContain("Shop-00000000", Case.Sensitive);
		error.Message.ShouldContain($"{WorkspaceKey.For(Alpha)} ({Alpha})", Case.Sensitive);
		error.Message.ShouldContain($"{WorkspaceKey.For(Beta)} ({Beta})", Case.Sensitive);
		error.Message.ShouldContain("workspace with the solution's path", Case.Sensitive);
	}

	/// <summary>With nothing loaded there are no keys to offer, and the failure says why and what to send.</summary>
	[Test]
	public void A_key_with_nothing_loaded_says_to_pass_the_path()
	{
		var error = Should.Throw<McpException>(() => WorkspaceManager.ByKey("Shop-00000000", []))
			.ShouldBeOfType<McpException>();

		error.Message.ShouldContain("none is loaded", Case.Sensitive);
		error.Message.ShouldContain(". Pass workspace with the solution's path", Case.Sensitive);
	}

	/// <summary>
	/// Four bytes of hash collide, however rarely, and a key two loaded solutions share is no answer
	/// about either: the caller hears both paths. The pair is found by search rather than written
	/// down, so the test proves the case it names rather than one somebody computed once.
	/// </summary>
	[Test]
	public void A_key_two_loaded_solutions_share_is_refused_naming_both()
	{
		var (first, second) = Colliding();

		var error = Should.Throw<McpException>(() => WorkspaceManager.ByKey(WorkspaceKey.For(first), [first, second]))
			.ShouldBeOfType<McpException>();

		error.Message.ShouldContain(first, Case.Sensitive);
		error.Message.ShouldContain(second, Case.Sensitive);
		error.Message.ShouldContain("Pass workspace with the path", Case.Sensitive);
	}

	/// <summary>
	/// A key that names nothing is refused even where a path in the same call would have resolved:
	/// the caller named a workspace, and answering from another one is the failure routing exists to
	/// prevent.
	/// </summary>
	[Test]
	public void An_unknown_key_never_falls_through_to_the_paths()
	{
		var directory = Path.Combine(Path.GetTempPath(), "rosemcp-tests", $"keyed-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);

		try
		{
			var solution = Path.Combine(directory, "Shop.slnx");
			File.WriteAllText(solution, "<Solution />");
			var manager = Manager();

			manager.WorkspaceFor(WorkspaceHints.From(null, RootedPath.Absolute(solution))).ShouldBe(
				solution, StringCompareShould.IgnoreCase);

			Should.Throw<McpException>(
				() => manager.WorkspaceFor(WorkspaceHints.From(null, "Shop-00000000", RootedPath.Absolute(solution))))
				.ShouldBeOfType<McpException>();
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	/// <summary>A blank key is an argument a client filled in without meaning to, not a key.</summary>
	[Test]
	public void A_blank_key_names_nothing()
	{
		WorkspaceHints.From(null, "  ").WorkspaceKey.ShouldBeNull();
		WorkspaceHints.ForNewFile(null, string.Empty, null).WorkspaceKey.ShouldBeNull();
	}

	/// <summary>
	/// What a key looks like, which is how one sent as <c>workspace</c> is told from a path naming
	/// nothing: every key the broker makes has the shape, and a file name or a bare word does not.
	/// </summary>
	[Test]
	public void A_key_is_told_from_a_path_by_its_shape()
	{
		WorkspaceKey.HasShape(WorkspaceKey.For(Alpha)).ShouldBeTrue();
		WorkspaceKey.HasShape("My-Shop-1A2B3C4D").ShouldBeTrue();
		WorkspaceKey.HasShape(" Shop-1a2b3c4d ").ShouldBeTrue();

		WorkspaceKey.HasShape("Shop.slnx").ShouldBeFalse();
		WorkspaceKey.HasShape("Shop-1a2b3c4").ShouldBeFalse();
		WorkspaceKey.HasShape("Shop-1a2b3c4g").ShouldBeFalse();
		WorkspaceKey.HasShape("-1a2b3c4d").ShouldBeFalse();
		WorkspaceKey.HasShape("Shop_1a2b3c4d").ShouldBeFalse();
		WorkspaceKey.HasShape(null).ShouldBeFalse();
	}

	/// <summary>
	/// Every tool that declares <c>workspaceKey</c> hands it to the routing. Sending it beside
	/// <c>workspace</c> is refused there, before any worker is started, so a tool that declared the
	/// parameter and built its hints without it would answer from the path instead -- and the refusal
	/// is what proves the key arrived.
	/// </summary>
	[Test]
	public async Task Every_tool_taking_a_key_refuses_a_workspace_named_twice()
	{
		var manager = Manager();
		var paths = new CallerPaths(Options());
		object[] tools = [new BrokerTools(manager, paths), new BrokerAnalysisTools(manager, paths)];

		var keyed = tools
			.SelectMany(tool => tool.GetType()
				.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
				.Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
				.Where(method => method.GetParameters().Any(parameter => parameter.Name == "workspaceKey"))
				.Select(method => (Tool: tool, Method: method)))
			.ToArray();

		keyed.Length.ShouldBeGreaterThan(20);

		foreach (var (tool, method) in keyed)
		{
			var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name;

			var error = await Should.ThrowAsync<McpException>(() => Invoke(tool, method), $"{name} did not refuse")
				.OfExactType();

			error.Message.ShouldContain("Both workspace", Case.Sensitive, $"{name} answered something else: {error.Message}");
		}
	}

	/// <summary>
	/// Calls a tool with both workspace arguments and a harmless value for everything else, surfacing
	/// what it throws whether it throws before or after returning its task.
	/// </summary>
	private static async Task Invoke(object tool, MethodInfo method)
	{
		var arguments = method.GetParameters().Select(Argument).ToArray();
		Task task;

		try
		{
			task = (Task)method.Invoke(tool, arguments)!;
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
			throw;
		}

		await task;
	}

	private static object? Argument(ParameterInfo parameter)
	{
		if (parameter.Name == "workspace") return "Shop.slnx";
		if (parameter.Name == "workspaceKey") return "Shop-00000000";

		var type = parameter.ParameterType;

		if (type == typeof(CancellationToken)) return CancellationToken.None;
		if (type == typeof(IProgress<ProgressNotificationValue>)) return new Progress<ProgressNotificationValue>();
		if (parameter.HasDefaultValue) return parameter.DefaultValue;
		if (type == typeof(string)) return "x";
		if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);

		return type.IsValueType ? Activator.CreateInstance(type) : null;
	}

	/// <summary>Two solution paths of one name whose keys collide, found by counting through directories.</summary>
	private static (string First, string Second) Colliding()
	{
		var root = NowhereDirectory.Path();
		var seen = new Dictionary<string, string>(StringComparer.Ordinal);

		for (var index = 0; ; index++)
		{
			var path = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture), "Shop.slnx");
			var key = WorkspaceKey.For(path);

			if (seen.TryGetValue(key, out var earlier)) return (earlier, path);

			seen.Add(key, path);
		}
	}

	private static WorkspaceManager Manager()
	{
		var options = Options();

		return new WorkspaceManager(
			options,
			new CallerPaths(options),
			NullLoggerFactory.Instance,
			NullLogger<WorkspaceManager>.Instance);
	}

	private static IOptions<BrokerOptions> Options() =>
		Microsoft.Extensions.Options.Options.Create(new BrokerOptions { DefaultWorkspaceRoot = NowhereDirectory.Path() });
}
