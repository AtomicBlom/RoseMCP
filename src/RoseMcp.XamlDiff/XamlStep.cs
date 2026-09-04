namespace RoseMcp.XamlDiff;

/// <summary>What one <see cref="XamlStep"/> does.</summary>
public enum XamlStepKind
{
	/// <summary>Build an instance of a type and keep it in a slot.</summary>
	Create,

	/// <summary>Set a property on whatever is in a slot.</summary>
	SetProperty,

	/// <summary>Put what is in a slot into something's children, at an index.</summary>
	AddChild,
}

/// <summary>
/// One primitive step in building an element in a running app.
/// <para>
/// <see cref="Target"/> is a slot -- <c>$0</c>, <c>$1</c> -- for anything being built, and an
/// element's address for the one step that attaches the finished subtree to the app. A slot names an
/// instance that exists only for the length of one apply, which is what lets a nested element be
/// created, filled and then handed to its parent.
/// </para>
/// </summary>
public sealed record XamlStep
{
	public required XamlStepKind Kind { get; init; }

	/// <summary>The slot being built, or the address being added to.</summary>
	public required string Target { get; init; }

	/// <summary>The type to build, for <see cref="XamlStepKind.Create"/>. The local name as the markup spelled it.</summary>
	public string? TypeName { get; init; }

	/// <summary>The property, for <see cref="XamlStepKind.SetProperty"/>.</summary>
	public string? Property { get; init; }

	/// <summary>The value's inferred type, for <see cref="XamlStepKind.SetProperty"/>; empty when unknown.</summary>
	public string? ValueType { get; init; }

	/// <summary>The value, for <see cref="XamlStepKind.SetProperty"/>.</summary>
	public string? Value { get; init; }

	/// <summary>The slot holding the child, for <see cref="XamlStepKind.AddChild"/>.</summary>
	public string? Child { get; init; }

	/// <summary>Where the child goes among its new siblings, for <see cref="XamlStepKind.AddChild"/>.</summary>
	public int Index { get; init; }
}
