namespace RoseMcp.Contracts;

/// <summary>One diagnostic that has a fix available, and what the fix is called.</summary>
public sealed record AvailableCodeFix
{
	public required string DiagnosticId { get; init; }

	public required string Message { get; init; }

	public required string Severity { get; init; }

	public required string FilePath { get; init; }

	public required int Line { get; init; }

	/// <summary>
	/// The fixes offered, as their authors titled them. More than one means a choice: pass the title
	/// to apply a particular one rather than the first.
	/// </summary>
	public required IReadOnlyList<string> FixTitles { get; init; }

	/// <summary>
	/// Whether this fix can be applied to a whole project or solution at once. Where it cannot, the
	/// fix still works one occurrence at a time.
	/// </summary>
	public required bool SupportsFixAll { get; init; }
}
