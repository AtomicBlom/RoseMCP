using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.UnitTests;

/// <summary>
/// How long a reader has before the stop they are looking at ends, and when to ask for more of it.
/// <para>
/// Every stop is on a timer, because an unattended one would wedge somebody's application. So a
/// stack can vanish while it is being read, and the only thing separating that from a bug is having
/// said it was about to.
/// </para>
/// </summary>
public sealed class HoldCountdownTests
{
	private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

	[Test]
	public void A_stop_on_its_safety_timer_says_when_it_lets_go()
	{
		Assert.Equal("auto-continues in 12s", HoldCountdown.Describe(held: false, TimeSpan.FromSeconds(12)));
	}

	[Test]
	public void A_stop_somebody_is_holding_says_so_and_for_how_long()
	{
		Assert.Equal($"held for you{Format.Separator}47s left", HoldCountdown.Describe(held: true, TimeSpan.FromSeconds(47)));
	}

	/// <summary>
	/// A deadline that has passed reads as "now" rather than counting upwards: the resume is in
	/// flight, and a negative number would be a reader watching a clock run backwards.
	/// </summary>
	[Test]
	public void A_deadline_already_past_reads_as_now()
	{
		Assert.Equal($"held for you{Format.Separator}now left", HoldCountdown.Describe(held: true, TimeSpan.FromSeconds(-5)));
	}

	[Test]
	[Arguments(120, false)]
	[Arguments(61, false)]
	[Arguments(59, true)]
	[Arguments(-1, true)]
	public void A_hold_is_renewed_before_it_runs_out(int secondsLeft, bool expected)
	{
		Assert.Equal(expected, HoldCountdown.ShouldRenew(Now.AddSeconds(secondsLeft), Now));
	}
}
