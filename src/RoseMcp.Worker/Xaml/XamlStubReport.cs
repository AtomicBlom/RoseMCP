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
}
