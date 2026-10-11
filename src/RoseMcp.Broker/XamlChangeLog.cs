using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Every live edit made to one debug session's app that took, kept for the session: when, who asked, the element,
/// the property and what it was before and after. What the Inspector's XAML view marks and filters on, and what it
/// names the person or agent session by.
/// <para>
/// Written by the apply calls an agent session makes, which can run side by side, and read by a window's long poll
/// every second, so it is a list under a lock rather than state a reader can catch half written. Bounded at
/// <see cref="Capacity"/>, oldest first out, with the count let go kept beside it: an agent in an edit loop makes
/// several changes a minute, and a session is allowed to last all day. In memory and nowhere else, because a handle
/// means nothing to another run of the app; it goes with the session that holds it.
/// </para>
/// </summary>
public sealed class XamlChangeLog
{
	/// <summary>How many changes are kept: far more than a person reads back through, and a bounded cost per session.</summary>
	public const int Capacity = 500;

	/// <summary>What a change is filed under as a tool, which is the one tool that makes one.</summary>
	public const string ApplyTool = ToolNames.XamlApply;

	private readonly Lock _gate = new();
	private readonly Queue<Recorded> _changes = new();
	private long _sequence;
	private long _dropped;

	/// <summary>Pulsed as changes are recorded, so a window waiting on the list is answered at once.</summary>
	public ChangeSignal Changed { get; } = new();

	/// <summary>One change and who made it, kept by the actor rather than by name so a window names them as they are now.</summary>
	/// <param name="Change">The change, its agent not yet named.</param>
	/// <param name="By">Who made it.</param>
	public sealed record Recorded(LiveXamlChange Change, LiveAppActor By);

	/// <summary>
	/// Records each edit of <paramref name="result"/> that took. An edit that did not take changed nothing, so it is
	/// not a change to show; the caller's answer already says why it did not.
	/// </summary>
	/// <returns>How many changes were recorded.</returns>
	public int Record(LiveXamlApplyResult result, LiveAppActor by, string? file, DateTime nowUtc)
	{
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(by);

		var recorded = 0;

		lock (_gate)
		{
			foreach (var edit in result.Results.Where(edit => edit.Status == "applied"))
			{
				var change = new LiveXamlChange
				{
					Sequence = ++_sequence,
					ChangedUtc = nowUtc,
					Person = by.IsPerson,
					Tool = ApplyTool,
					Kind = edit.Kind,
					Target = edit.Target,
					Handle = edit.Handle,
					Property = edit.Property,
					OldValue = edit.OldValue,
					NewValue = edit.Kind == "ClearProperty" ? null : edit.Value,
					File = file,
				};

				_changes.Enqueue(new Recorded(change, by));
				recorded++;

				while (_changes.Count > Capacity)
				{
					_changes.Dequeue();
					_dropped++;
				}
			}
		}

		if (recorded > 0) Changed.Pulse();

		return recorded;
	}

	/// <summary>Every change kept, oldest first, and how many were let go before them.</summary>
	public (IReadOnlyList<Recorded> Changes, long Dropped) Snapshot()
	{
		lock (_gate)
		{
			return ([.. _changes], _dropped);
		}
	}
}
