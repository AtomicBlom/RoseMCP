using System.Collections.ObjectModel;

namespace RoseMcp.Ui.Core;

/// <summary>
/// Brings a bound collection in line with what the broker now reports, updating the rows that are
/// still there rather than replacing them.
/// <para>
/// Replacing is the obvious implementation and it is wrong in a way that is easy to miss, because
/// the data looks right. A progress bar recreated four times a second never animates; an expander
/// the reader just opened closes under them; a tree node loses its expansion; a selected row loses
/// its selection. Every list in every RoseMCP window has at least one of those properties, so this
/// is written once rather than per row type.
/// </para>
/// </summary>
public static class Rows
{
	/// <summary>
	/// Removes rows whose key is gone, adds rows for keys that are new, and hands the rest to
	/// <paramref name="update"/> in place. Rows end in the order the sources are in.
	/// </summary>
	/// <param name="rows">The bound collection, mutated.</param>
	/// <param name="sources">What the collection should now hold.</param>
	/// <param name="rowKey">A row's identity.</param>
	/// <param name="sourceKey">A source's identity, comparable with a row's.</param>
	/// <param name="create">Builds a row for a source that has no row yet.</param>
	/// <param name="update">Brings an existing row in line with its source.</param>
	public static void Merge<TRow, TSource, TKey>(
		ObservableCollection<TRow> rows,
		IReadOnlyList<TSource> sources,
		Func<TRow, TKey> rowKey,
		Func<TSource, TKey> sourceKey,
		Func<TSource, TRow> create,
		Action<TRow, TSource> update)
		where TKey : notnull
	{
		// Backwards, because removing shifts every index after it.
		for (var index = rows.Count - 1; index >= 0; index--)
		{
			var key = rowKey(rows[index]);
			var stillThere = sources.Any(source => sourceKey(source).Equals(key));

			if (!stillThere) rows.RemoveAt(index);
		}

		for (var index = 0; index < sources.Count; index++)
		{
			var source = sources[index];
			var key = sourceKey(source);
			var existing = rows.FirstOrDefault(row => rowKey(row).Equals(key));

			if (existing is null)
			{
				// Clamped, because the removals above can leave the collection shorter than the
				// position this source wants, and Insert past the end throws.
				rows.Insert(Math.Min(index, rows.Count), create(source));
				continue;
			}

			update(existing, source);
		}
	}
}
