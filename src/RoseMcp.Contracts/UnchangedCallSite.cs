namespace RoseMcp.Contracts;

/// <summary>
/// A use of a member that a signature change did not touch, and why.
/// <para>
/// Reported rather than left out, because "compiles" and "correct" part company here. A forwarder
/// that still passes the default of a parameter it should now be passing through compiles perfectly
/// and is the bug hardest to see: the evidence behind this tool is one optional parameter threaded
/// through six layers, where the layers that compiled were the ones nobody thought to check.
/// </para>
/// </summary>
public sealed record UnchangedCallSite
{
	public required SourceLocation Location { get; init; }

	/// <summary>Why it was left: nothing needed doing, or nothing could safely be done.</summary>
	public required string Reason { get; init; }
}
