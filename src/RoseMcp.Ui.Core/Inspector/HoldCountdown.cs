namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// How long a reader has before the target they are looking at goes away, and when to ask for more.
/// <para>
/// A stop is on a timer whichever way it was taken: a breakpoint auto-continues so an unattended
/// hit cannot wedge somebody's application, and an operator hold is capped for the same reason. So
/// the countdown is not decoration -- a stack that vanishes with no warning is a reader losing their
/// place and not knowing why.
/// </para>
/// <para>
/// One place for it because two surfaces say it: the tray and the inspector's header describe a
/// session, and the stack pane describes the stop it is reading. Two copies of the sentence would
/// disagree the first time either was reworded.
/// </para>
/// </summary>
public static class HoldCountdown
{
	/// <summary>How close to the deadline a hold is renewed.</summary>
	public static readonly TimeSpan RenewWithin = TimeSpan.FromSeconds(60);

	/// <summary>
	/// How long before a stop ends on its own, and who is keeping it.
	/// </summary>
	/// <param name="held">Whether a person is holding it, rather than its own safety timer.</param>
	/// <param name="remaining">How long is left, which <see cref="Format.Countdown"/> floors at now.</param>
	public static string Describe(bool held, TimeSpan remaining) => held
		? $"held for you{Format.Separator}{Format.Countdown(remaining)} left"
		: $"auto-continues in {Format.Countdown(remaining)}";

	/// <summary>
	/// Whether a hold is close enough to expiring to be worth renewing.
	/// <para>
	/// Asked on a timer rather than scheduled for the deadline, because the answer has to survive a
	/// renewal that fails, a machine that slept, and a stop replaced by a newer one. A minute is
	/// comfortably more than a renewal takes and comfortably less than the hold, so a reader never
	/// sees the gap.
	/// </para>
	/// </summary>
	public static bool ShouldRenew(DateTime deadlineUtc, DateTime utcNow) =>
		deadlineUtc - utcNow < RenewWithin;
}
