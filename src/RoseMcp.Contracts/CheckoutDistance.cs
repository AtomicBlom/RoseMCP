namespace RoseMcp.Contracts;

/// <summary>
/// How far a local build's checkout has moved past the commit the build was made from: its own
/// <c>HEAD</c>, and <c>origin/main</c> as that checkout last fetched it.
/// <para>
/// Each count is separately unknown, with the reason, because each fails on its own: a checkout with
/// no remote still has a <c>HEAD</c>, and a commit that was rebased away is in neither.
/// </para>
/// </summary>
public sealed record CheckoutDistance
{
	/// <summary>The working tree that was asked.</summary>
	public required string Checkout { get; init; }

	/// <summary>How far the checkout's <c>HEAD</c> is past the running build.</summary>
	public required CommitsPast Head { get; init; }

	/// <summary>How far <c>origin/main</c>, as last fetched, is past the running build.</summary>
	public required CommitsPast OriginMain { get; init; }

	/// <summary>When git was asked. The answer is kept briefly rather than asked for on every read.</summary>
	public required DateTimeOffset CheckedUtc { get; init; }
}
