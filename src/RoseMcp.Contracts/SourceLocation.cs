namespace RoseMcp.Contracts;

/// <summary>A position in source, one-based to match editors and humans.</summary>
public sealed record SourceLocation
{
	public required string FilePath { get; init; }

	public required int Line { get; init; }

	public required int Column { get; init; }

	/// <summary>The source line itself, so a caller can judge a hit without opening the file.</summary>
	public string? Preview { get; init; }

	/// <summary>Set when the location is inside source-generated code rather than a file on disk.</summary>
	public string? GeneratedHintName { get; init; }
}
