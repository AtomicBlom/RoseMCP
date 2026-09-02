namespace RoseMcp.XamlDiff;

/// <summary>
/// The result of diffing two XAML versions: the edits to apply, and notes about anything detected but
/// not turned into an edit (a reorder, an ambiguous match), so nothing is silently dropped.
/// </summary>
public sealed record XamlDiffResult
{
	public IReadOnlyList<XamlEdit> Edits { get; init; } = [];

	public IReadOnlyList<string> Notes { get; init; } = [];
}
