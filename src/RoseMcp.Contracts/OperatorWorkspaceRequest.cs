namespace RoseMcp.Contracts;

/// <summary>
/// Which workspace a person's window acts on -- reloading or closing it -- by the solution path its row was shown
/// with. In the body rather than the route, because a Windows path carries backslashes and colons no route segment
/// survives legibly.
/// </summary>
public sealed record OperatorWorkspaceRequest
{
	/// <summary>The solution, absolute, as <see cref="WorkspaceScopedResult.Workspace"/> on the row named it.</summary>
	public required string Workspace { get; init; }
}
