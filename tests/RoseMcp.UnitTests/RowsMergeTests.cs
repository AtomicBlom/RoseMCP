using System.Collections.ObjectModel;
using RoseMcp.Ui.Core;

namespace RoseMcp.UnitTests;

/// <summary>
/// Bringing a bound collection in line with what the broker now reports.
/// <para>
/// The assertion that matters is the one about identity. A merge that produced the right values by
/// replacing every row would pass any test of the data and still be wrong, because what a window
/// loses then is invisible to a test of the data: a progress bar recreated four times a second
/// never animates, an expander closes under the reader, a selection goes. So these check that the
/// row objects survive, not only that the values do.
/// </para>
/// </summary>
public sealed class RowsMergeTests
{
	/// <summary>A row with an identity and something a refresh could otherwise reset.</summary>
	private sealed class Row(int id, string label)
	{
		public int Id { get; } = id;

		public string Label { get; private set; } = label;

		/// <summary>Stands in for the expansion or selection a real row would lose on replacement.</summary>
		public bool Expanded { get; set; }

		public void Update(string label) => Label = label;
	}

	private static void Merge(ObservableCollection<Row> rows, params (int Id, string Label)[] sources) =>
		Rows.Merge(
			rows,
			sources,
			row => row.Id,
			source => source.Id,
			source => new Row(source.Id, source.Label),
			(row, source) => row.Update(source.Label));

	[Test]
	public void Fills_an_empty_collection_in_source_order()
	{
		ObservableCollection<Row> rows = [];

		Merge(rows, (1, "one"), (2, "two"));

		Assert.Equal([1, 2], rows.Select(row => row.Id));
		Assert.Equal(["one", "two"], rows.Select(row => row.Label));
	}

	/// <summary>
	/// The whole point. The row object is the same instance afterwards, so whatever a window put on
	/// it is still there.
	/// </summary>
	[Test]
	public void Updates_a_row_in_place_rather_than_replacing_it()
	{
		ObservableCollection<Row> rows = [];
		Merge(rows, (1, "working"));

		var original = rows[0];
		original.Expanded = true;

		Merge(rows, (1, "done"));

		Assert.Same(original, rows[0]);
		Assert.Equal("done", rows[0].Label);
		Assert.True(rows[0].Expanded, "the row a reader had expanded is the same row afterwards");
	}

	[Test]
	public void Drops_a_row_whose_source_is_gone()
	{
		ObservableCollection<Row> rows = [];
		Merge(rows, (1, "one"), (2, "two"), (3, "three"));

		Merge(rows, (1, "one"), (3, "three"));

		Assert.Equal([1, 3], rows.Select(row => row.Id));
	}

	/// <summary>
	/// Removals shorten the collection before insertions run, so a source at position two can be
	/// inserted into a collection of length one. Clamping is what stops that throwing.
	/// </summary>
	[Test]
	public void Inserts_past_the_end_of_a_collection_the_removals_shortened()
	{
		ObservableCollection<Row> rows = [];
		Merge(rows, (1, "one"), (2, "two"), (3, "three"));

		Merge(rows, (1, "one"), (9, "nine"), (8, "eight"));

		Assert.Equal([1, 9, 8], rows.Select(row => row.Id));
	}

	[Test]
	public void Empties_a_collection_whose_sources_have_all_gone()
	{
		ObservableCollection<Row> rows = [];
		Merge(rows, (1, "one"), (2, "two"));

		Merge(rows);

		Assert.Empty(rows);
	}
}
