namespace RoseMcp.Contracts;

/// <summary>
/// The element a user picked by clicking it in the running app (issue #18). <see cref="Selected"/> is
/// false, with a <see cref="Detail"/> saying why, when select mode has not been entered or nobody has
/// clicked yet. <see cref="Handle"/> is the same stable handle the visual tree reports, so the usual
/// property and hot-reload tools take it directly -- that is what turns "the thing I clicked" into
/// something the agent can read and change.
/// </summary>
public sealed record LiveXamlSelection
{
	public bool Selected { get; init; }

	public ulong Handle { get; init; }

	public string? TypeName { get; init; }

	/// <summary>The element's <c>x:Name</c>, when it has one.</summary>
	public string? Name { get; init; }

	public string? Detail { get; init; }
}
