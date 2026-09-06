using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>One parameter as it will be, and what it was before.</summary>
public sealed record PlannedParameter
{
	public required ParameterSyntax Declaration { get; init; }

	public required string Name { get; init; }

	/// <summary>Where this parameter was in the old list, or null when it is new.</summary>
	public required int? WasAt { get; init; }

	/// <summary>Where it is in the new list.</summary>
	public required int IsAt { get; init; }

	/// <summary>
	/// True when the new declaration gives it a default, which is what decides whether a call site
	/// that says nothing about it still compiles.
	/// </summary>
	public required bool HasDefault { get; init; }

	/// <summary>True when an existing parameter is still at the index it was.</summary>
	public bool KeptItsPlace => WasAt == IsAt;
}
