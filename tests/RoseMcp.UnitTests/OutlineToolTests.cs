using RoseMcp.Broker.Tools;
using RoseMcp.Worker.Tools;

namespace RoseMcp.UnitTests;

/// <summary>
/// What rose_outline does for a caller who passes nothing but a type or a file.
/// <para>
/// The cheap answer is the default because the caller who does not know the switches is the one who
/// pays for whatever they default to, and the type an outline is worth most on is a large one, where
/// signatures and documentation are most of the answer and overrun what a client will accept. A
/// caller who wants either has just been told the member names and can ask about the ones it cares
/// about.
/// </para>
/// <para>
/// The broker forwards every argument, so the worker's defaults are reached only by a worker run on
/// its own -- which is exactly where a drift between the two would go unnoticed, so they are held
/// equal here.
/// </para>
/// </summary>
public sealed class OutlineToolTests
{
	[Test]
	public void Defaults_to_the_cheap_form()
	{
		var defaults = Defaults(typeof(BrokerAnalysisTools));

		defaults["includeDocumentation"].ShouldBe(false);
		defaults["includeSignatures"].ShouldBe(false);
		defaults["includeInherited"].ShouldBe(false);
		defaults["members"].ShouldBeNull();
		defaults["maxMembers"].ShouldBe(OutlineService.DefaultMaxMembers);
	}

	[Test]
	public void The_worker_defaults_every_argument_the_way_the_broker_does()
	{
		var broker = Defaults(typeof(BrokerAnalysisTools));
		var worker = Defaults(typeof(NavigationTools));

		// The broker alone picks the workspace; everything else it forwards.
		broker.Remove("workspace");

		worker.Keys.Order().ShouldBe(broker.Keys.Order());

		foreach (var (name, value) in broker)
		{
			worker[name].ShouldBe(value, $"rose_outline's {name} defaults differently in the worker");
		}
	}

	/// <summary>The default of every argument a caller passes, by name.</summary>
	private static Dictionary<string, object?> Defaults(Type tools)
	{
		var method = tools.GetMethod("OutlineAsync").ShouldNotBeNull();

		return method.GetParameters()
			.Where(parameter => parameter.HasDefaultValue && parameter.ParameterType != typeof(CancellationToken))
			.ToDictionary(parameter => parameter.Name!, parameter => parameter.DefaultValue);
	}
}
