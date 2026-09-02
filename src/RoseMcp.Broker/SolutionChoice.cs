namespace RoseMcp.Broker;

/// <summary>
/// Which solution a path resolved to, what else was in the running, and why this one won.
/// <para>
/// The reason exists to be logged. A choice that turns out to be the wrong one is close to
/// undiagnosable from the answer alone -- a search of the wrong solution returns nothing, which
/// reads exactly like a search of the right one finding nothing -- so the log has to carry enough
/// to reconstruct the decision without reproducing it.
/// </para>
/// </summary>
public sealed record SolutionChoice
{
	/// <summary>The solution, or the bare project, a worker should own.</summary>
	public required string SolutionPath { get; init; }

	/// <summary>Everything that was considered, including the winner. One entry, usually.</summary>
	public required IReadOnlyList<string> Candidates { get; init; }

	/// <summary>How it won, in a few words fit for the end of a log line.</summary>
	public required string Reason { get; init; }

	/// <summary>Whether anything was actually decided between, which is what makes it worth logging.</summary>
	public bool WasContested => Candidates.Count > 1;
}
