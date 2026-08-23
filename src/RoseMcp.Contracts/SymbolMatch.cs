namespace RoseMcp.Contracts;

public sealed record SymbolMatch
{
	public required string Name { get; init; }

	public required string Kind { get; init; }

	public required string Signature { get; init; }

	public required string Project { get; init; }

	public SourceLocation? Location { get; init; }
}
