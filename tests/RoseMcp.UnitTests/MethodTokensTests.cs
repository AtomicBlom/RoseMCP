using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// How a debugger read gets from a property's name to the value behind it. A read here runs none of
/// the target's code, so a property is reachable only through the field the compiler declared for it,
/// and a path a person writes names the property, not that field.
/// </summary>
public sealed class MethodTokensTests
{
	[Test]
	[Arguments("Name", "<Name>k__BackingField")]
	[Arguments("Count", "<Count>k__BackingField")]
	public void Names_the_field_behind_an_auto_property(string property, string field)
	{
		MethodTokens.BackingFieldOf(property).ShouldBe(field);
		MethodTokens.PropertyBackedBy(field).ShouldBe(property);
	}

	/// <summary>A field somebody wrote is its own name, angle brackets or not.</summary>
	[Test]
	[Arguments("_name")]
	[Arguments("<>k__BackingField")]
	[Arguments("<Name>k__BackingFieldX")]
	[Arguments("<Name>5__2")]
	public void Leaves_a_field_somebody_wrote_alone(string field)
	{
		MethodTokens.PropertyBackedBy(field).ShouldBeNull();
	}

	/// <summary>
	/// The backing field and the property are both in metadata, read from the module on disk, and a
	/// property with a getter of its own has the property and no field.
	/// </summary>
	[Test]
	public void Reads_auto_and_computed_properties_from_metadata()
	{
		var module = typeof(Carrier).Assembly.Location;
		var type = typeof(Carrier).MetadataToken;

		MethodTokens.FieldToken(module, type, MethodTokens.BackingFieldOf(nameof(Carrier.Auto))).ShouldNotBeNull();
		MethodTokens.DeclaresProperty(module, type, nameof(Carrier.Auto)).ShouldBeTrue();

		MethodTokens.FieldToken(module, type, MethodTokens.BackingFieldOf(nameof(Carrier.Computed))).ShouldBeNull();
		MethodTokens.DeclaresProperty(module, type, nameof(Carrier.Computed)).ShouldBeTrue();

		MethodTokens.DeclaresProperty(module, type, "Missing").ShouldBeFalse();
	}

	private sealed class Carrier
	{
		public int Auto { get; set; } = 1;

		public int Computed => Auto * 2;
	}
}
