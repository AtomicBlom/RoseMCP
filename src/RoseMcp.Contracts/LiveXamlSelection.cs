namespace RoseMcp.Contracts;

/// <summary>
/// The element that was picked by clicking it in the running app (issue #18) -- either from the
/// in-app toolbar's Select Element button or after an agent armed select mode.
/// <see cref="Selected"/> is false, with a <see cref="Detail"/> saying why, when nobody has picked
/// yet. <see cref="Handle"/> is the same stable handle the visual tree reports, so the usual property
/// and hot-reload tools take it directly -- that is what turns "the thing I clicked" into something
/// the agent can read and change.
/// </summary>
public sealed record LiveXamlSelection
{
	public bool Selected { get; init; }

	/// <summary>
	/// Whether select mode is armed right now, read from the toolbar rather than remembered: the
	/// person can arm and cancel it themselves, so what this side last asked for proves nothing.
	/// </summary>
	public bool Armed { get; init; }

	public ulong Handle { get; init; }

	public string? TypeName { get; init; }

	/// <summary>The element's <c>x:Name</c>, when it has one.</summary>
	public string? Name { get; init; }

	public string? Detail { get; init; }
}
