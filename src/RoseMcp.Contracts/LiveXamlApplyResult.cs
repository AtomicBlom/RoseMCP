namespace RoseMcp.Contracts;

/// <summary>
/// The outcome of a live edit: the app's XAML was diffed against a previous version and the resulting
/// edits applied to the live tree. <see cref="Results"/> is one entry per computed edit with its
/// outcome; <see cref="Applied"/> counts the ones that took. <see cref="Notes"/> carries what the diff
/// engine could not do (an unmatched element, say). <see cref="Detail"/> is set, with no results, when
/// the apply could not run at all -- the XAML would not parse, or the provider could not be injected.
/// </summary>
public sealed record LiveXamlApplyResult
{
	public int Applied { get; init; }

	public int Total => Results.Count;

	public IReadOnlyList<LiveXamlEditResult> Results { get; init; } = [];

	public IReadOnlyList<string> Notes { get; init; } = [];

	public string? Detail { get; init; }
}
