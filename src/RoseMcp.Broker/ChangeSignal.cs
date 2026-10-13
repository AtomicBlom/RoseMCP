namespace RoseMcp.Broker;

/// <summary>
/// Says that something a view shows has changed, to whoever is waiting on <see cref="Next"/> at that moment.
/// <para>
/// A wake-up rather than a record: it carries nothing, a pulse nobody is waiting for is lost, and a waiter that
/// missed one still learns of the change from the view itself, which <see cref="LongPoll"/> reads again on a short
/// timer whatever is pulsed. So a pulse only makes a long poll answer sooner, and a change the owner did not pulse
/// for -- a row expiring, another broker's file growing -- is still seen, a moment later.
/// </para>
/// </summary>
public sealed class ChangeSignal
{
	private readonly ChangeSignal[] _sources = [];
	private TaskCompletionSource _next = New();

	/// <summary>A signal its own owner pulses.</summary>
	public ChangeSignal()
	{
	}

	private ChangeSignal(ChangeSignal[] sources) => _sources = sources;

	/// <summary>
	/// A signal that completes at the next pulse of any of <paramref name="sources"/>, for a view made of several
	/// owners' state: the debug sessions, which a call changes and a person's start changes without a call.
	/// </summary>
	public static ChangeSignal Either(params ChangeSignal[] sources) => new([.. sources]);

	/// <summary>
	/// A task that completes at the next <see cref="Pulse"/>, or the next of any source's. Take it before reading the
	/// view, so a change between the read and the wait is not missed.
	/// </summary>
	public Task Next => _sources.Length == 0
		? Volatile.Read(ref _next).Task
		: Task.WhenAny(_sources.Select(source => source.Next));

	/// <summary>Completes the task every current waiter holds, and starts a new one for the next. Never throws and never blocks.</summary>
	public void Pulse() => Interlocked.Exchange(ref _next, New()).TrySetResult();

	/// <summary>Continuations run off the pulsing thread, which is a tool call's and must not run a long poll's answer inline.</summary>
	private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
