namespace RoseMcp.Contracts;

/// <summary>The fixable diagnostics found in one file.</summary>
public sealed record CodeFixList : WorkspaceScopedResult
{
	public required long Revision { get; init; }

	public required string FilePath { get; init; }

	public required IReadOnlyList<AvailableCodeFix> Fixes { get; init; }

	/// <summary>
	/// Diagnostics present in the file that no fixer offers to repair, by id. Reported so the absence
	/// of a fix reads as "nothing here can fix that" rather than "there is nothing wrong".
	/// <para>
	/// Includes ids a fixer claims and then declines: a provider that registers for a diagnostic and
	/// offers no action is, from here, the same as no provider. Counting only unclaimed ids left
	/// those diagnostics out of this list and out of <see cref="Fixes"/> both, which reported them as
	/// not being there at all.
	/// </para>
	/// </summary>
	public required IReadOnlyList<string> UnfixableIds { get; init; }

	public required IReadOnlyList<string> Notices { get; init; }
}
