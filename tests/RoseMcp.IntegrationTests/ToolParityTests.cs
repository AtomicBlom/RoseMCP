using System.ComponentModel;
using System.Reflection;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// That the broker and the live-app host declare the same arguments for the tools they share.
/// <para>
/// The forwarding chain is the shape of every change here, and four layers spell each of these
/// arguments: the broker declares one, the session forwards it, the host declares it again and the
/// session object takes it. Two of them had drifted -- justMyXaml carried a different sentence on
/// each declaring end -- so a caller reading the broker's copy and a maintainer reading the host's
/// were told different things about one argument.
/// </para>
/// <para>
/// Here rather than in the unit suite because neither test project takes a compile reference on the
/// host: it is net10.0-windows, and the broker reaches it as an MCP client over a pipe. The assembly
/// is loaded from the build output instead, which is what puts this test's cost here even though it
/// starts no process and loads no solution.
/// </para>
/// </summary>
public sealed class ToolParityTests
{
	[Fact]
	public void The_broker_and_the_live_app_host_declare_the_same_arguments()
	{
		var broker = Parameters(typeof(RoseMcp.Broker.Tools.BrokerTools).Assembly);
		var host = Parameters(HostAssembly());

		Assert.NotEmpty(ToolNames.LiveAppPairs);

		foreach (var (declared, forwarded) in ToolNames.LiveAppPairs)
		{
			Assert.True(broker.ContainsKey(declared), $"the broker does not declare {declared}");
			Assert.True(host.ContainsKey(forwarded), $"the host does not declare {forwarded}");

			// One string per pair, so a failure shows both argument lists rather than reporting that
			// two arrays differ.
			Assert.Equal(
				$"{declared}: {string.Join(" | ", host[forwarded])}",
				$"{declared}: {string.Join(" | ", broker[declared])}");
		}
	}

	/// <summary>
	/// The host's managed assembly from the build output. Loaded rather than referenced, because a
	/// compile reference on a Windows-only host would pull the whole live-app half into both test
	/// projects.
	/// </summary>
	private static Assembly HostAssembly()
	{
		var path = Path.Combine(
			TestToolchain.RepositoryRoot(),
			"src",
			"RoseMcp.LiveApp",
			"bin",
			TestToolchain.Configuration(),
			"net10.0-windows",
			"RoseMcp.LiveApp.dll");

		Assert.True(File.Exists(path), $"the live-app host is not built at {path}");

		return Assembly.LoadFrom(path);
	}

	/// <summary>
	/// Each tool's arguments as a client is shown them: name and description, in order.
	/// <para>
	/// Two are dropped rather than compared. sessionId is how the broker picks the host to send to,
	/// and exists only on the end that picks; progress and cancellationToken are the SDK's own and
	/// never reach the schema.
	/// </para>
	/// </summary>
	private static Dictionary<string, string[]> Parameters(Assembly assembly)
	{
		var declared = new Dictionary<string, string[]>(StringComparer.Ordinal);

		foreach (var type in Types(assembly))
		{
			if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is not { Name: { Length: > 0 } name }) continue;

				declared[name] = [.. method.GetParameters().Where(Compared).Select(Describe)];
			}
		}

		return declared;
	}

	/// <summary>
	/// Whether an argument is one both ends are expected to declare.
	/// <para>
	/// Five are not, and each for a stated reason. sessionId is how the broker picks the host to send
	/// to and exists only on the end that picks; progress and cancellationToken are the SDK's own and
	/// never reach the schema. element and root are the one place the two ends deliberately take
	/// different things: the broker accepts a handle, an x:Name or an address and resolves it, and the
	/// host takes the handle that comes out -- an address is a position among siblings, so only the
	/// side holding the tree it came from could resolve it, and that side is the broker.
	/// </para>
	/// </summary>
	private static bool Compared(ParameterInfo parameter) =>
		parameter.Name is not (
			"sessionId" or "progress" or "cancellationToken"
			or "element" or "handle" or "root" or "rootName");

	private static string Describe(ParameterInfo parameter) =>
		$"{parameter.Name}: {parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty}";

	/// <summary>
	/// The types in an assembly, skipping any that will not load. The host targets Windows and pulls
	/// in projections this process has no need of, and only the tool types matter here.
	/// </summary>
	private static IEnumerable<Type> Types(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException loaded)
		{
			return loaded.Types.OfType<Type>();
		}
	}
}
