namespace RoseMcp.Symbols;

/// <summary>
/// Which compiled methods make up the body a reader thinks of as one method.
/// <para>
/// The reason this is not simply "the method" is the thing that makes a position picker worth
/// having at all. A breakpoint inside a lambda is a breakpoint in <c>&lt;&gt;c.&lt;Refresh&gt;b__3_0</c>,
/// and one after an <c>await</c> is in <c>&lt;Refresh&gt;d__3.MoveNext</c> -- so clicking a line in
/// what looks like <c>Refresh</c> has to be able to land in a method whose name nobody has ever
/// typed. Showing only the named method's own points means the lines somebody most wants to break on
/// are the ones the picker refuses.
/// </para>
/// <para>
/// Pure, over extents somebody else read, because the closure is the part that is easy to get subtly
/// wrong and impossible to see in a test that only asserts a breakpoint bound somewhere.
/// </para>
/// </summary>
public static class MethodRegion
{
	/// <summary>
	/// The extents belonging to one method: its own, the state machines written for it, and anything
	/// in the same file lying wholly inside the lines those cover.
	/// <para>
	/// Two rules rather than one, because the two cases are shaped differently. A state machine is
	/// joined by the link the PDB records, since its lines cover the whole method rather than sitting
	/// inside it. A lambda or a local function has no such link and needs none: its code is written
	/// inside the braces of the method that owns it, so containment says so.
	/// </para>
	/// <para>
	/// The state machines are a seed and not only a step, because an <c>async</c> method has no
	/// sequence points of its own at all -- every line of it belongs to the <c>MoveNext</c>. Starting
	/// from the named method's own extent therefore finds nothing for exactly the methods a reader
	/// most wants to look at, and the emptiness reads as a method with no code in it.
	/// </para>
	/// <para>
	/// Applied until nothing more joins, so a lambda inside an async method is reached -- it is
	/// contained in lines only after the state machine has brought those lines in.
	/// </para>
	/// </summary>
	/// <param name="extents">Every method in the module with debug information.</param>
	/// <param name="methodToken">The method somebody asked to see.</param>
	public static IReadOnlyList<MethodExtent> Of(IReadOnlyList<MethodExtent> extents, int methodToken)
	{
		var seeds = extents
			.Where(extent => extent.MethodToken == methodToken || extent.KickoffToken == methodToken)

			// The named method first where it has an extent, so the file and the order are its own.
			.OrderBy(extent => extent.MethodToken == methodToken ? 0 : 1)
			.ToList();

		if (seeds.Count == 0) return [];

		var file = seeds[0].File;
		var chosen = seeds.Where(extent => SameFile(extent, file)).ToList();

		// The asked-for token is in the set whether or not it has an extent, so a state machine
		// generated for it still matches on the kickoff link.
		var tokens = new HashSet<int>(chosen.Select(extent => extent.MethodToken)) { methodToken };

		var first = chosen.Min(extent => extent.FirstLine);
		var last = chosen.Max(extent => extent.LastLine);

		var candidates = extents
			.Where(extent => !tokens.Contains(extent.MethodToken))
			.Where(extent => SameFile(extent, file))
			.ToList();

		var joined = true;
		while (joined)
		{
			joined = false;

			for (var index = candidates.Count - 1; index >= 0; index--)
			{
				var candidate = candidates[index];
				var isStateMachine = candidate.KickoffToken is { } kickoff && tokens.Contains(kickoff);
				var isInside = candidate.FirstLine >= first && candidate.LastLine <= last;

				if (!isStateMachine && !isInside) continue;

				chosen.Add(candidate);
				tokens.Add(candidate.MethodToken);
				first = Math.Min(first, candidate.FirstLine);
				last = Math.Max(last, candidate.LastLine);
				candidates.RemoveAt(index);
				joined = true;
			}
		}

		return chosen;
	}

	/// <summary>
	/// The lines a set of extents covers together, or null for an empty set. The file is the first
	/// extent's, which is the method somebody asked about.
	/// </summary>
	public static (string File, int FirstLine, int LastLine)? Lines(IReadOnlyList<MethodExtent> region)
	{
		if (region.Count == 0) return null;

		return (region[0].File, region.Min(extent => extent.FirstLine), region.Max(extent => extent.LastLine));
	}

	/// <summary>
	/// Whether an extent came from the file the region is being read out of. A method can span files
	/// -- a partial method's halves, anything a generator wove together -- and lines from another one
	/// would be offered as positions in this one.
	/// </summary>
	private static bool SameFile(MethodExtent extent, string file) =>
		string.Equals(extent.File, file, StringComparison.OrdinalIgnoreCase);
}
