namespace RoseMcp.Contracts;

/// <summary>
/// What <c>GET /operator/hello</c> answers: which build the broker is, and for a local build how
/// far its checkout has moved on since. The first thing a window asks, so it can say which build it
/// is talking to and whether that is the one beside it.
/// </summary>
public sealed record OperatorHello
{
	/// <summary>The broker's own build.</summary>
	public required BuildIdentity Build { get; init; }

	/// <summary>
	/// How many commits the checkout the broker was built from has gained since, or null for a build
	/// that names no checkout -- a CI build, or one made outside git.
	/// </summary>
	public CheckoutDistance? Checkout { get; init; }
}
