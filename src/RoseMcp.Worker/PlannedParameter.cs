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

	/// <summary>
	/// True when it was declared <c>params</c>. A params parameter can hold several arguments at one
	/// call site, and several arguments cannot be written as one named argument -- so it is the one
	/// parameter whose position at a call site is not negotiable.
	/// </summary>
	public required bool WasParams { get; init; }

	/// <summary>True when an existing parameter is still at the index it was.</summary>
	public bool KeptItsPlace => WasAt == IsAt;
}
