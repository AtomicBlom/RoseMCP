namespace RoseMcp.Worker;

/// <summary>Which file to import into, and what.</summary>
public sealed record AddUsingRequest
{
	public required string FilePath { get; init; }

	/// <summary>
	/// Namespaces to ensure, written however they come to hand: <c>System.Text</c>,
	/// <c>using System.Text</c> or <c>using System.Text;</c> all name the same one.
	/// </summary>
	public required IReadOnlyList<string> Namespaces { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Compile the projects holding the file afterwards. Worth doing even here: an import usually
	/// only resolves things, but it can make a name ambiguous between two namespaces.
	/// </summary>
	public bool Verify { get; init; } = true;

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
