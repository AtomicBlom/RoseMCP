namespace RoseMcp.Contracts;

/// <summary>
/// The debug sessions the broker is supervising. A wrapper because MCP requires a tool's
/// <c>structuredContent</c> to be a JSON object: a tool returning a bare collection serialises to a
/// top-level array, which fails client-side schema validation and makes the tool uncallable.
/// </summary>
public sealed record LiveAppSessionList
{
	public IReadOnlyList<LiveAppSessionSummary> Sessions { get; init; } = [];
}
