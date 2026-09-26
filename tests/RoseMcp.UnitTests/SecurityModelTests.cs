using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// The security model names every tool a client is offered.
/// <para>
/// A threat model that describes a smaller program than the one that ships is worse than none, because
/// it reads as complete. A tool added without an entry is a capability whose gate nobody wrote down, and
/// a hand-kept list of every tool is stale as soon as the next one lands -- so this is what notices.
/// </para>
/// </summary>
public sealed class SecurityModelTests
{
	[Test]
	public void Every_advertised_tool_has_an_entry_in_the_security_model()
	{
		var document = File.ReadAllText(SecurityModelPath());

		foreach (var name in Advertised())
		{
			document.ShouldContain($"`{name}`", Case.Sensitive);
		}
	}

	/// <summary>
	/// Read from the registration, as <see cref="ToolSurfaceTests"/> does, because only the registration
	/// knows which half of the surface this operating system offers.
	/// </summary>
	private static string[] Advertised()
	{
		var services = new ServiceCollection();
		services.AddRoseMcpBroker();

		using var provider = services.BuildServiceProvider();

		return [.. provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool.Name)];
	}

	/// <summary>
	/// Found by walking up to the solution rather than copied into the build output, so the test reads
	/// the document a person reads and not a copy that can lag behind it.
	/// </summary>
	private static string SecurityModelPath()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
			{
				return Path.Combine(directory.FullName, "docs", "debug", "security-model.md");
			}
		}

		throw new InvalidOperationException($"No RoseMcp.slnx above {AppContext.BaseDirectory}.");
	}
}
