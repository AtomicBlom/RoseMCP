namespace RoslynMcp.Contracts;

/// <summary>
/// Everything a caller needs to decide whether to trust this workspace's answers. Deliberately
/// verbose about failure: a workspace that loaded but cannot see its source generators produces
/// confidently wrong results, so those causes are reported rather than left to be inferred from
/// suspiciously empty output.
/// </summary>
public sealed record WorkspaceStatusReport
{
	public required string SolutionPath { get; init; }

	public required WorkspaceState State { get; init; }

	/// <summary>
	/// Monotonic snapshot counter. Bumped by every mutation, absorbed file change, and reload.
	/// Two results carrying the same revision describe the same world.
	/// </summary>
	public required long Revision { get; init; }

	public required IReadOnlyList<ProjectStatus> Projects { get; init; }

	/// <summary>Diagnostics MSBuild reported while loading -- unresolved references, failed projects.</summary>
	public required IReadOnlyList<string> LoadDiagnostics { get; init; }

	/// <summary>
	/// Why this workspace is <see cref="WorkspaceState.Degraded"/>, each paired with the command
	/// that would fix it. Empty when the load is trustworthy.
	/// </summary>
	public required IReadOnlyList<string> DegradedReasons { get; init; }

	public RestoreReport? Restore { get; init; }

	public double LoadSeconds { get; init; }
}

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

/// <summary>What the worker did about restore before loading, and whether it worked.</summary>
public sealed record RestoreReport
{
	public required bool Ran { get; init; }

	/// <summary>Why restore was or was not run, in words a caller can act on.</summary>
	public required string Reason { get; init; }

	/// <summary>Null when restore did not run, so a skipped restore never reads as a failure.</summary>
	public bool? Succeeded { get; init; }

	/// <summary>Tail of the restore output, present only on failure.</summary>
	public string? Output { get; init; }
}
