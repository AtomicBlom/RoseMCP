namespace RoseMcp.IntegrationTests;

/// <summary>
/// The real clock, moved forward on demand, with a hook on every read. Timers stay real, so the
/// sweep still ticks at its interval; only what it takes "now" to be is steered.
/// </summary>
internal sealed class SteerableClock : TimeProvider
{
	private long _offsetTicks;

	/// <summary>Called on every read of the time, before it is taken.</summary>
	public Action? OnRead { get; set; }

	public void Jump(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

	public override DateTimeOffset GetUtcNow()
	{
		OnRead?.Invoke();
		return base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
	}
}
