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
}
