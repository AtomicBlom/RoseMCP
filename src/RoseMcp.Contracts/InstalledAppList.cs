namespace RoseMcp.Contracts;

/// <summary>The packaged apps installed for this user, as <c>GET /operator/debug/packages</c> answers.</summary>
public sealed record InstalledAppList
{
	/// <summary>Those in development mode first, each group by name.</summary>
	public IReadOnlyList<InstalledApp> Apps { get; init; } = [];

	/// <summary>Why the list is empty or short, where something said so: no packages on this platform.</summary>
	public string? Detail { get; init; }
}
