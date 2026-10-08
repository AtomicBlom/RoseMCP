using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// A read asked about several things in one call, each answered as the call for it alone would answer
/// it, from one snapshot.
/// <para>
/// The requests are independent, so one that is refused -- a name nothing declares, an overload not
/// picked -- is that entry's status and never stops the rest: refusing the call for it would send the
/// caller back to ask about the others one at a time, which is the turn count a list exists to save.
/// What a request's own mistake is, is what the read throws for one, an <see cref="ArgumentException"/>;
/// anything else is a fault in the workspace and fails the call as it would have for one request. What
/// is wrong with the call as a whole -- an empty list, a filter naming no project -- is checked before
/// any entry is answered, so it is one refusal rather than the same one on every entry.
/// </para>
/// <para>
/// What the workspace reconciled before reading is said once, on the batch: every answer would
/// otherwise repeat it, since every answer reads the same snapshot.
/// </para>
/// </summary>
public static class ReadBatches
{
	/// <summary>
	/// The requests a call named: <paramref name="names"/> where it was given, and otherwise the one
	/// position the call pointed at. Refuses a list with nothing in it, naming the argument, since an
	/// answer with no entries reads as a call that worked.
	/// </summary>
	/// <param name="names">The list argument as it arrived; null where it was not sent.</param>
	/// <param name="argument">The list argument's name, for the refusal.</param>
	/// <param name="position">What the call pointed at instead, or null where it pointed at nothing.</param>
	/// <param name="neither">What to say where the call named nothing at all.</param>
	public static IReadOnlyList<string> Requested(
		IReadOnlyList<string?>? names,
		string argument,
		string? position,
		string neither)
	{
		if (names is { Count: 0 })
		{
			throw new ArgumentException($"{argument} is empty. Name at least one, as [\"Namespace.Type.Member\"].");
		}

		var both = names is not null && position is not null;
		if (both)
		{
			throw new ArgumentException(
				$"Name {argument}, or point at one with filePath, line and column -- not both, since a name "
					+ "and a position that disagree would each answer about a different symbol.");
		}

		if (names is not null) return [.. names.Select(name => name ?? "")];

		return position is null ? throw new ArgumentException(neither) : [position];
	}

	/// <summary>Each request answered in order, from one snapshot.</summary>
	/// <param name="snapshot">The snapshot every answer reads.</param>
	/// <param name="requested">What was asked about, in order.</param>
	/// <param name="answer">The read for one request.</param>
	/// <param name="withoutNotices">
	/// The answer with the given notices taken out of its own, which is how the snapshot's notices are
	/// said once on the batch rather than on every answer.
	/// </param>
	/// <param name="cancellationToken">Cancels the batch between requests.</param>
	/// <param name="listed">
	/// Whether the requests came as a list. One position or one file is not: a call asking about it has no
	/// other entry to keep, so its refusal is the call's, and reaches the broker as a failure -- which is
	/// where a path the answering solution does not compile gains the name of the one that does.
	/// </param>
	public static async Task<ReadBatch<T>> EachAsync<T>(
		WorkspaceSnapshot snapshot,
		IReadOnlyList<string> requested,
		Func<string, Task<T>> answer,
		Func<T, IReadOnlySet<string>, T> withoutNotices,
		CancellationToken cancellationToken,
		bool listed = true)
		where T : class
	{
		var shared = snapshot.Notices.ToHashSet(StringComparer.Ordinal);
		var results = new List<ReadEntry<T>>();

		foreach (var request in requested)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (string.IsNullOrWhiteSpace(request))
			{
				results.Add(new ReadEntry<T> { Requested = request, Status = "refused: an empty name names nothing." });
				continue;
			}

			try
			{
				var answered = await answer(request);
				results.Add(new ReadEntry<T> { Requested = request, Status = "found", Answer = withoutNotices(answered, shared) });
			}
			catch (ArgumentException refused) when (listed)
			{
				results.Add(new ReadEntry<T> { Requested = request, Status = $"refused: {refused.Message}" });
			}
		}

		return new ReadBatch<T>
		{
			Revision = snapshot.Revision,
			Results = results,
			Notices = [.. snapshot.Notices],
		};
	}

	/// <summary>The notices less those the batch says once.</summary>
	public static IReadOnlyList<string> Own(IReadOnlyList<string> notices, IReadOnlySet<string> shared) =>
		[.. notices.Where(notice => !shared.Contains(notice))];
}
