namespace RoseMcp.Contracts;

/// <summary>
/// The outcome of stopping a running target where it stands.
/// <para>
/// <see cref="LiveExecutionReport.Stop"/> is the stop it created, which is what a caller reads
/// frames against -- so pausing and asking where the target is takes one round trip rather than
/// two, and the answer cannot describe a different stop from the one just taken.
/// </para>
/// </summary>
public sealed record LivePauseResult : LiveExecutionReport
{
	/// <summary>
	/// Whether this call is what stopped the target.
	/// <para>
	/// False when there was nothing to stop, and false when the target stopped on its own first --
	/// a breakpoint reached in the moment between asking and the stop taking effect. Both leave a
	/// perfectly good stop behind, which is why this is reported beside
	/// <see cref="LiveExecutionReport.Stop"/> rather than instead of it.
	/// </para>
	/// </summary>
	public required bool Paused { get; init; }
}
