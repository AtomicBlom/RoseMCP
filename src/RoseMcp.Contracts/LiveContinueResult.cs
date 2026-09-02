namespace RoseMcp.Contracts;

/// <summary>The outcome of a continue request: whether a target was actually held and resumed.</summary>
public sealed record LiveContinueResult
{
	public required bool Continued { get; init; }
}
