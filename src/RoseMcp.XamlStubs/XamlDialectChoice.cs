namespace RoseMcp.XamlStubs;

/// <summary>
/// Which dialect a project turned out to be, and on what evidence. The reason is carried so an
/// ambiguous answer can be reported rather than quietly acted on.
/// </summary>
public sealed record XamlDialectChoice
{
	/// <summary>Null when no known XAML framework is referenced, in which case nothing is generated.</summary>
	public required IXamlDialect? Dialect { get; init; }

	public required string Reason { get; init; }

	/// <summary>True when more than one framework was referenced and the tie had to be broken.</summary>
	public required bool WasAmbiguous { get; init; }

	/// <summary>
	/// The assembly whose own source generator compiles this project's markup inside the workspace,
	/// or null when the markup compiler runs only in a real build -- which is the case stubs exist
	/// for. When set, nothing is stubbed: that generator's partials are already in the compilation,
	/// and a second set is a duplicate of every member.
	/// </summary>
	public string? CompiledInWorkspaceBy { get; init; }
}
