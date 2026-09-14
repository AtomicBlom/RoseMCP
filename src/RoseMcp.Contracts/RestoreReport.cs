namespace RoseMcp.Contracts;

/// <summary>What the worker did about restore before loading, and whether it worked.</summary>
public sealed record RestoreReport
{
	public required bool Ran { get; init; }

	/// <summary>Why restore was or was not run, in words a caller can act on.</summary>
	public required string Reason { get; init; }

	/// <summary>Null when restore did not run, so a skipped restore never reads as a failure.</summary>
	public bool? Succeeded { get; init; }

	/// <summary>Tail of the restore output, present only on failure.</summary>
	public string? Output { get; init; }

	/// <summary>
	/// Projects that still have no usable restore output once restore has been dealt with, by file
	/// name, whether it ran or was skipped.
	/// <para>
	/// The postcondition asked rather than assumed, and the two are different questions.
	/// <c>dotnet restore</c> restores the projects it understands, passes silently over the ones it
	/// does not -- non-SDK csproj, which on a UWP solution is most of them -- and exits 0 either way,
	/// so <see cref="Succeeded"/> is a fact about the command and not about the solution. Measured on
	/// Drawboard's monorepo it reported success with 62 of 105 projects holding no
	/// <c>project.assets.json</c> at all.
	/// </para>
	/// <para>
	/// A project in this list resolves no package references, and that does not present as a restore
	/// problem. It presents as types, analyzers and generators that are simply absent -- the silent
	/// nothing this server exists to catch, one layer earlier than the analyzer checks look for it.
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Unrestored { get; init; } = [];
}
