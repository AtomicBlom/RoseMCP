namespace RoseMcp.Contracts;

/// <summary>The fixable diagnostics found in one file.</summary>
public sealed record CodeFixList
{
	public required long Revision { get; init; }

	public required string FilePath { get; init; }

	public required IReadOnlyList<AvailableCodeFix> Fixes { get; init; }

	/// <summary>
	/// Diagnostics present in the file that no fixer offers to repair, by id. Reported so the absence
	/// of a fix reads as "nothing here can fix that" rather than "there is nothing wrong".
	/// </summary>
	public required IReadOnlyList<string> UnfixableIds { get; init; }

	public required IReadOnlyList<string> Notices { get; init; }
}
