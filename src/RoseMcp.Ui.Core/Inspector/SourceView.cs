using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Turns a method's source and its breakable positions into rows somebody clicks.
/// <para>
/// The whole of the interaction is here rather than in the pane, because the part that is easy to
/// get wrong is not the drawing. A line can hold more than one place to stop -- two lambdas in a
/// chained call, an <c>await</c> and the statement around it -- and those are in different methods,
/// so a row that silently picked one of them would set a breakpoint in code the reader had not
/// pointed at. Being shown as separate rows is what makes the choice theirs.
/// </para>
/// </summary>
public static class SourceView
{
	/// <summary>
	/// One row per line, plus a row for every extra method with instructions on that line.
	/// <para>
	/// Positions in the same method on one line collapse to a single row addressing the earliest of
	/// them, which is where the statement starts. Splitting those out would be a list of offsets
	/// nobody could choose between: they are the same line of the same method, and a reader has
	/// nothing to tell one from another.
	/// </para>
	/// <para>
	/// A row is named with its method only when that is not the method being read, so the note means
	/// "this is somewhere else" rather than repeating the heading on every line.
	/// </para>
	/// </summary>
	/// <param name="source">The method as the host read it.</param>
	public static IReadOnlyList<SourceLineRow> Build(LiveMethodSource source)
	{
		if (source.Lines.Count == 0) return [];

		var width = (source.FirstLine + source.Lines.Count - 1).ToString().Length;
		var byLine = source.Positions
			.GroupBy(position => position.Line)
			.ToDictionary(group => group.Key, group => group.ToList());

		var rows = new List<SourceLineRow>();

		for (var index = 0; index < source.Lines.Count; index++)
		{
			var line = source.FirstLine + index;
			var text = source.Lines[index];

			if (!byLine.TryGetValue(line, out var positions))
			{
				rows.Add(new SourceLineRow
				{
					Line = line,
					LineLabel = line.ToString().PadLeft(width),
					Text = text,
				});

				continue;
			}

			// Grouped by the method the instructions are in, keeping the order the positions arrived
			// in, so the method being read comes before the lambdas written inside it.
			var owners = positions.GroupBy(position => position.DisplayName, StringComparer.Ordinal);

			var first = true;
			foreach (var owner in owners)
			{
				var earliest = owner.MinBy(position => position.IlOffset)!;
				var elsewhere = !string.Equals(owner.Key, source.DisplayName, StringComparison.Ordinal);

				rows.Add(new SourceLineRow
				{
					Line = line,
					LineLabel = first ? line.ToString().PadLeft(width) : string.Empty,
					Text = first ? text : string.Empty,
					Location = earliest.Location,
					Note = elsewhere ? owner.Key : string.Empty,
					OffsetLabel = Format.IlOffset(earliest.IlOffset),
					IsContinuation = !first,
				});

				first = false;
			}
		}

		return rows;
	}
}
