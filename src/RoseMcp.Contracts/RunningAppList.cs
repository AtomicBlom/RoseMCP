namespace RoseMcp.Contracts;

/// <summary>The .NET processes a person could debug, as <c>GET /operator/debug/apps</c> answers.</summary>
public sealed record RunningAppList
{
	/// <summary>Those with a window first, each group by name.</summary>
	public IReadOnlyList<RunningApp> Apps { get; init; } = [];

	/// <summary>Whether the list holds every .NET process this user runs, rather than those with a window.</summary>
	public bool Every { get; init; }

	/// <summary>Why the list is empty or short, where something said so: no way to read processes on this platform.</summary>
	public string? Detail { get; init; }
}
