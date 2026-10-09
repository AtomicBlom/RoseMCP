using RoseMcp.Fixture.Absent;

namespace Broken;

/// <summary>Uses the package that never restored, so every name from it is CS0246.</summary>
public sealed class Uses
{
	public Gadget First { get; } = new();

	public Gadget Second { get; } = new();
}