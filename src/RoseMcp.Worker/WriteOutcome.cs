using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>What a set of solution changes did, or would do.</summary>
public sealed record WriteOutcome
{
	/// <summary>Each file written, with its absolute path, the lines it changed and what was normalised.</summary>
	public required IReadOnlyList<ChangedFile> ChangedFiles { get; init; }

	public required string Diff { get; init; }

	/// <summary>
	/// What the write did beyond what it was asked to. Empty almost always; the case it exists for is
	/// an edit that changed lines nothing it was asked to do reaches.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];

	/// <summary>The absolute paths of <see cref="ChangedFiles"/>, in the same order.</summary>
	public IReadOnlyList<string> Paths => [.. ChangedFiles.Select(file => file.FilePath)];

	/// <summary>
	/// <see cref="ChangedFiles"/> with the file at <paramref name="first"/> leading, where it is one of
	/// them. A result that is about one file names it as the first entry rather than in a field of its
	/// own, so the path is said once.
	/// </summary>
	public IReadOnlyList<ChangedFile> Leading(string first) =>
		[
			.. ChangedFiles.Where(file => string.Equals(file.FilePath, first, StringComparison.OrdinalIgnoreCase)),
			.. ChangedFiles.Where(file => !string.Equals(file.FilePath, first, StringComparison.OrdinalIgnoreCase)),
		];
}
