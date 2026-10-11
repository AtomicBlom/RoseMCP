using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// The one order every list of operations keeps, on an agent's card and in the Activity view alike: running calls
/// first, newest first; then finished ones by when they finished, newest first, and by when they started where two
/// finished together. A broker starting sits where it started, so a restart is a line in the list rather than a gap.
/// <para>
/// By finish rather than by start because the question a person brings is what just happened: a long call that
/// started ten minutes ago and ended a moment ago is news, and ordered by its start it would be ten minutes down
/// the list. There is no separate "now" list, because a call moving from running to finished would then jump from
/// one list to the other; here it moves from the top of the list to the top of the finished ones.
/// </para>
/// </summary>
public static class OperationOrder
{
	/// <summary><paramref name="entries"/> in the order a list shows them.</summary>
	public static IReadOnlyList<ActivityEntry> Sort(IEnumerable<ActivityEntry> entries) =>
		[.. entries
			.OrderByDescending(entry => entry.Outcome == ActivityOutcome.Running)
			.ThenByDescending(entry => entry.Outcome == ActivityOutcome.Running ? entry.StartedUtc : entry.FinishedUtc ?? entry.StartedUtc)
			.ThenByDescending(entry => entry.StartedUtc)
			.ThenByDescending(entry => entry.Id, StringComparer.Ordinal)];

	/// <summary>
	/// Brings a bound list of rows in line with <paramref name="entries"/>, sorted, updating the rows that are still
	/// there so an opened row stays open and keeps the body it read.
	/// </summary>
	public static void Merge(ObservableCollection<OperationRow> rows, IEnumerable<ActivityEntry> entries, DateTimeOffset now)
	{
		var sorted = Sort(entries.DistinctBy(entry => entry.Id, StringComparer.Ordinal));

		Rows.Merge(
			rows,
			sorted,
			row => row.Id,
			entry => entry.Id,
			entry => new OperationRow(entry, now),
			(row, entry) => row.Update(entry, now));
	}
}
