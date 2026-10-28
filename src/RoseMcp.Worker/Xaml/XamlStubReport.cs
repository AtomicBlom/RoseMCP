namespace RoseMcp.Worker.Xaml;

/// <summary>
/// What stub generation did to one project, so workspace status can say it out loud. Synthesised
/// semantics that nobody is told about are worse than none: an agent cannot judge an answer it does
/// not know is approximate.
/// </summary>
public sealed record XamlStubReport
{
	/// <summary>Null when no known XAML framework was referenced and nothing was generated.</summary>
	public required string? Dialect { get; init; }

	/// <summary>The evidence the dialect was chosen on, including "assumed" when it was a guess.</summary>
	public required string DialectReason { get; init; }

	/// <summary>True when several frameworks were referenced and the tie had to be broken.</summary>
	public required bool DialectAmbiguous { get; init; }

	public required int MarkupFileCount { get; init; }

	public required int StubbedClassCount { get; init; }

	/// <summary>Elements whose type this project cannot see, so their fields were left out.</summary>
	public required IReadOnlyList<string> UnresolvedTypes { get; init; }

	/// <summary>Markup that needed no stub, or could not have one, with the reason.</summary>
	public required IReadOnlyList<string> Skipped { get; init; }

	/// <summary>The classes the markup names, where no dialect was chosen and so nothing was stubbed.</summary>
	public IReadOnlyList<string> UnstubbedClasses { get; init; } = [];

	/// <summary>
	/// The generator compiling this project's markup inside the workspace, by its assembly's name, where no dialect was
	/// chosen and every class the markup names already has the <c>InitializeComponent</c> that generator wrote. Null
	/// otherwise. Filled in by the worker, which can see every generator's output, not by the stub generator, which can
	/// see none but its own.
	/// </summary>
	public string? CompiledInWorkspaceBy { get; init; }
}
