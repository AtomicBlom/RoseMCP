using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>What compiling after an edit found, or that nothing was compiled.</summary>
public sealed record Verification
{
	public static readonly Verification NotRun = new();

	/// <summary>
	/// False when no compilation happened. Kept separate from an empty introduced list because the
	/// two look identical to a caller and mean opposite things.
	/// </summary>
	public bool Ran { get; init; }

	/// <summary>Errors that exist now and did not before.</summary>
	public IReadOnlyList<DiagnosticEntry> Introduced { get; init; } = [];

	/// <summary>Errors that existed before and do not now.</summary>
	public int ResolvedCount { get; init; }

	/// <summary>Every error the checked projects report now, whoever caused it.</summary>
	public int TotalCount { get; init; }

	/// <summary>The projects that were compiled.</summary>
	public IReadOnlyList<string> Projects { get; init; } = [];
}
