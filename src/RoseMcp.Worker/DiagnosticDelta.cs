using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Which errors an edit introduced and how many it resolved, given the errors before and after it.
/// <para>
/// Matched in two passes. The first pairs an error with the one it became, by following its position
/// through the edit's text changes: that is the only thing that tells two identical errors apart,
/// and without it an edit that adds a fourth use of an ambiguous name reports one of the three that
/// were already there -- the one furthest down, which had only moved -- as the one it introduced.
/// The count comes out right either way; which entry is listed is what goes wrong, and the entry is
/// what a caller goes to fix.
/// </para>
/// <para>
/// The second pass matches what is left on everything except position, so an error the edit's own
/// rewrite passed over -- its position fell inside the changed text, which has no answer for where it
/// went -- is still recognised as the same error rather than as one resolved and one introduced.
/// </para>
/// </summary>
public static class DiagnosticDelta
{
	public static (IReadOnlyList<DiagnosticEntry> Introduced, int Resolved) Compare(
		IReadOnlyList<DiagnosticEntry> before,
		IReadOnlyList<DiagnosticEntry> after,
		TextMovement movement)
	{
		var consumed = new bool[before.Count];
		var placed = Index(before, entry => movement.Map(entry.FilePath, entry.Line, entry.Column) is { } moved
			? Placed(entry, moved.Line, moved.Column)
			: null);

		var unplaced = new List<DiagnosticEntry>();

		foreach (var entry in after)
		{
			if (Take(placed, Placed(entry, entry.Line, entry.Column), consumed)) continue;

			unplaced.Add(entry);
		}

		var loose = Index(before, Key);
		var introduced = new List<DiagnosticEntry>();

		foreach (var entry in unplaced)
		{
			// Counted rather than matched, so two identical errors in one file are two errors: fixing
			// one of them is a real change and has to show as one.
			if (Take(loose, Key(entry), consumed)) continue;

			introduced.Add(entry);
		}

		return (introduced, consumed.Count(taken => !taken));
	}

	private static Dictionary<string, Queue<int>> Index(IReadOnlyList<DiagnosticEntry> entries, Func<DiagnosticEntry, string?> key)
	{
		var index = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);

		for (var i = 0; i < entries.Count; i++)
		{
			if (key(entries[i]) is not { } value) continue;

			if (!index.TryGetValue(value, out var queue)) index[value] = queue = new Queue<int>();

			queue.Enqueue(i);
		}

		return index;
	}

	/// <summary>Takes the first entry under <paramref name="key"/> that the other pass has not already taken.</summary>
	private static bool Take(Dictionary<string, Queue<int>> index, string key, bool[] consumed)
	{
		if (!index.TryGetValue(key, out var queue)) return false;

		while (queue.TryDequeue(out var i))
		{
			if (consumed[i]) continue;

			consumed[i] = true;

			return true;
		}

		return false;
	}

	/// <summary>
	/// The project is part of the key, so a multi-targeted file's error under one framework is never
	/// paired with the same error under another.
	/// </summary>
	private static string Key(DiagnosticEntry entry) => $"{entry.Id}|{entry.Project}|{entry.FilePath}|{entry.Message}";

	private static string Placed(DiagnosticEntry entry, int line, int column) => $"{Key(entry)}|{line}:{column}";
}
