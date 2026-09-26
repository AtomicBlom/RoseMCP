using RoseMcp.Broker;

namespace RoseMcp.UnitTests;

/// <summary>
/// How the broker's session with a child it started is opened. The probe for <c>server/discover</c>
/// is never timed out on its own, because a probe cancelled partway through its send leaves half a
/// frame on the child's stdin, and the child then cannot read the <c>initialize</c> that follows.
/// </summary>
public sealed class ChildHostHandshakeTests
{
	/// <summary>
	/// A worker's session: the probe runs under the handshake budget alone, and the budget is the one
	/// the broker was configured with rather than the SDK's sixty seconds.
	/// </summary>
	[Test]
	public void Bounds_the_probe_by_the_handshake_budget_alone()
	{
		var options = ChildHostHandshake.Options(TimeSpan.FromMinutes(3));

		Assert.Equal(Timeout.InfiniteTimeSpan, options.DiscoverProbeTimeout);
		Assert.Equal(TimeSpan.FromMinutes(3), options.InitializationTimeout);
	}

	/// <summary>A live-app host's session, which keeps the SDK's budget and still never times out the probe.</summary>
	[Test]
	public void Keeps_the_sdk_budget_where_none_is_given()
	{
		var options = ChildHostHandshake.Options();

		Assert.Equal(Timeout.InfiniteTimeSpan, options.DiscoverProbeTimeout);
		Assert.Equal(new ModelContextProtocol.Client.McpClientOptions().InitializationTimeout, options.InitializationTimeout);
	}
}
