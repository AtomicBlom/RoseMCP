namespace RoseMcp.Contracts;

/// <summary>
/// The element that was picked by clicking it in the running app (issue #18) -- either from the
/// in-app toolbar's Select Element button or after an agent armed select mode.
/// <see cref="Selected"/> is false, with a <see cref="Detail"/> saying why, when nobody has picked
/// yet. <see cref="Handle"/> is the same stable handle the visual tree reports, so the usual property
/// and live-edit tools take it directly -- that is what turns "the thing I clicked" into something
/// the agent can read and change.
/// </summary>
public sealed record LiveXamlSelection
{
	public bool Selected { get; init; }

	/// <summary>
	/// Whether the overlay is capturing the pointer right now, read from the toolbar rather than
	/// remembered: the person can arm and cancel a mode themselves, so what this side last asked for
	/// proves nothing. True for every mode that lays a layer over the app, because what a caller
	/// needs from this is whether the app can still be clicked; <see cref="Mode"/> says which mode.
	/// </summary>
	public bool Armed { get; init; }

	/// <summary>
	/// What the overlay is doing with the pointer: <c>idle</c>, <c>select</c> while it waits for a
	/// click to pick an element, or <c>rulers</c> while it measures from the picked one.
	/// <para>
	/// A word rather than a flag for each mode, so a caller is not obliged to know every mode the
	/// toolbar has in order to ask about the one it cares about.
	/// </para>
	/// </summary>
	public string Mode { get; init; } = "idle";

	/// <summary>
	/// Whether picks currently prefer the app's own markup over a control template's parts. Read from
	/// the toolbar, like <see cref="Armed"/>, because its toggle can change it without this side
	/// being told.
	/// </summary>
	public bool JustMyXaml { get; init; } = true;

	public ulong Handle { get; init; }

	public string? TypeName { get; init; }

	/// <summary>The element's <c>x:Name</c>, when it has one.</summary>
	public string? Name { get; init; }

	/// <summary>
	/// How to name the picked element to a tool that changes one; see LiveXamlNode.Address. This is
	/// what turns a click into an edit: <see cref="Handle"/> reads it, and this changes it, including
	/// where the markup never gave it a name.
	/// </summary>
	public string? Address { get; init; }

	public string? Detail { get; init; }

	/// <summary>
	/// The whole stack under the click, topmost first; <see cref="Handle"/> is the first of them.
	/// Walk down it for a templated child, or up it for the container that holds what was clicked.
	/// </summary>
	public IReadOnlyList<LiveXamlSelectionCandidate> Candidates { get; init; } = [];
}
