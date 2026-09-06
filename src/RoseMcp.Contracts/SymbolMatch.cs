namespace RoseMcp.Contracts;

public sealed record SymbolMatch
{
	public required string Name { get; init; }

	public required string Kind { get; init; }

	/// <summary>
	/// This match as an address: pass it back as <c>symbol</c> to any rose_* tool. The signature beside
	/// it is for reading -- it carries the return type and the parameter names, and neither parses.
	/// </summary>
	public string? Address { get; init; }

	public required string Signature { get; init; }

	public required string Project { get; init; }

	public SourceLocation? Location { get; init; }
}
