namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// The broker's phase, with the reason a start failed, which is the one phase that carries one. Built only
/// through the members here, so a failure always has a reason and no other phase can be given one.
/// </summary>
public sealed record BrokerState
{
	private BrokerState(BrokerPhase phase, string? failedBecause)
	{
		Phase = phase;
		FailedBecause = failedBecause;
	}

	/// <summary>Being built and bound.</summary>
	public static BrokerState Starting { get; } = new(BrokerPhase.Starting, null);

	/// <summary>Taking calls.</summary>
	public static BrokerState Serving { get; } = new(BrokerPhase.Serving, null);

	/// <summary>Stopping on the way out.</summary>
	public static BrokerState Stopping { get; } = new(BrokerPhase.Stopping, null);

	/// <summary>A start that failed, and why. A reason left empty is said as unknown rather than as nothing.</summary>
	public static BrokerState Failed(string? reason) =>
		new(BrokerPhase.Failed, string.IsNullOrWhiteSpace(reason) ? "the reason is not known; the log may say" : reason.Trim());

	/// <summary>Which phase.</summary>
	public BrokerPhase Phase { get; }

	/// <summary>Why the start failed, for <see cref="BrokerPhase.Failed"/>; null for every other phase.</summary>
	public string? FailedBecause { get; }

	/// <summary>Whether it takes calls.</summary>
	public bool IsServing => Phase == BrokerPhase.Serving;
}
