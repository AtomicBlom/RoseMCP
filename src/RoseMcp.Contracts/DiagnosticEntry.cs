namespace RoseMcp.Contracts;

/// <summary>One diagnostic, located in a way a caller can act on.</summary>
public sealed record DiagnosticEntry
{
	public required string Id { get; init; }

	public required string Severity { get; init; }

	public required string Message { get; init; }

	public required string Project { get; init; }

	/// <summary>
	/// Path of the file the diagnostic is in. For generated code this is the synthetic path Roslyn
	/// gives the generated tree, which exists nowhere on disk.
	/// </summary>
	public string? FilePath { get; init; }

	/// <summary>One-based, to match what editors and humans use.</summary>
	public int Line { get; init; }

	public int Column { get; init; }

	/// <summary>
	/// Set when the diagnostic is inside source-generated code. Read the file with
	/// rose_read_generated_document -- it is not on disk, so ordinary file reads will not find it.
	/// </summary>
	public string? GeneratedHintName { get; init; }

	/// <summary>Documentation link, where the analyzer supplies one.</summary>
	public string? HelpLink { get; init; }
}
