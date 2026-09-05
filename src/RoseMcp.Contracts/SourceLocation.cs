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

	/// <summary>
	/// The member this location sits inside, as a signature. What turns a flat list of forty
	/// references into "used by these six methods", which is the question a caller actually had.
	/// </summary>
	public string? ContainingMember { get; init; }

	/// <summary>The project compiling the file, so a reference can be placed without opening it.</summary>
	public string? Project { get; init; }

	/// <summary>
	/// True where that project references a test framework. A use from a test is a different fact
	/// from a use in the product -- it usually means the symbol can change and the test follows.
	/// </summary>
	public bool IsTestProject { get; init; }
}
