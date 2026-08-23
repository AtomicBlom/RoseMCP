namespace RoseMcp.Contracts;

/// <summary>Per-project load result, including the generator accounting that makes silent failures visible.</summary>
public sealed record ProjectStatus
{
	public required string Name { get; init; }

	public required string FilePath { get; init; }

	public string? TargetFramework { get; init; }

	/// <summary>False when the design-time build failed. Semantic answers for this project are unreliable.</summary>
	public required bool LoadedSuccessfully { get; init; }

	public required int DocumentCount { get; init; }

	public required int AdditionalDocumentCount { get; init; }

	public required int AnalyzerReferenceCount { get; init; }

	/// <summary>Source generators actually instantiated from this project's analyzer references.</summary>
	public required int GeneratorCount { get; init; }

	/// <summary>Documents those generators produced. Zero alongside a non-zero generator count is worth a look.</summary>
	public required int GeneratedDocumentCount { get; init; }

	/// <summary>
	/// Projects this one references with OutputItemType="Analyzer" whose output never reached the
	/// analyzer list -- almost always because they have not been built yet.
	/// </summary>
	public required IReadOnlyList<string> MissingAnalyzerOutputs { get; init; }
}
