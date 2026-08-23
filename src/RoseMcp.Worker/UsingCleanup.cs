using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>What was removed, and the solution with it gone.</summary>
public sealed record UsingCleanup
{
	public required Solution Solution { get; init; }

	/// <summary>The directives dropped, as written, for reporting.</summary>
	public required IReadOnlyList<string> Removed { get; init; }
}
