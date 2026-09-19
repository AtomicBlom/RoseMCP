using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// The islands in a type: sets of members that would move together, worked out from what they
/// touch rather than from where they were written.
/// <para>
/// Two readings, because one algorithm answering both answers neither, and they find the same kind
/// of thing by different evidence. <em>What shares state</em> is a partition: members reading the
/// same fields form an island, and a type whose islands do not overlap is several types in one
/// file. <em>What is private to one member</em> is not a partition at all. A helper reached only
/// through one method belongs to it however much else it goes on to call, and no way of cutting a
/// graph into pieces can see that, because there is no cut to make -- the helper really is
/// connected to everything it reaches.
/// </para>
/// <para>
/// Both were needed on this repository. Splitting a debugger session came from the state: the
/// members reading the inspector shared no field with the members reading the breakpoint table.
/// Splitting enum editing out of a static class with no state at all came from the other: every path
/// to five helpers ran through one method, while the helpers <em>they</em> called were shared with
/// the rest of the file and had to stay where they were.
/// </para>
/// <para>
/// The line ranges are the half that makes this worth a tool rather than a metric. A group written
/// in one block lifts out in an afternoon; the same group scattered over three ranges a thousand
/// lines apart is a different job, and nothing in a member list says which one is in front of you.
/// </para>
/// </summary>
internal static class Cohesion
{
	/// <summary>
	/// The share of the members touching state that has to touch one field before it counts as the
	/// type's own rather than a group's. A field two groups of five use says the groups are joined; a
	/// field nine of ten use says nothing, and counting it joins everything to everything.
	/// </summary>
	private const double SpineShare = 0.5;

	/// <summary>
	/// The smallest world worth calling a member's own. Two is a method and the helper somebody
	/// extracted for readability; three is where what sits under one member starts to have a name.
	/// </summary>
	private const int OwnedFloor = 3;

	/// <summary>How many owners to report. Past the first few they are nested inside each other.</summary>
	private const int OwnedListed = 8;

	/// <summary>
	/// The share of a type past which owning something says nothing. The front door reaches
	/// everything by being the front door, and reporting that it owns the type is a tautology
	/// dressed as a finding.
	/// </summary>
	private const double OwnedCeiling = 0.7;

	/// <summary>
	/// The islands in one type, largest first, and the fields too widely read to tell anything
	/// apart. An empty list is the usual answer and a real one.
	/// </summary>
	internal static async Task<TypeIslands> OfAsync(
		INamedTypeSymbol type,
		Solution solution,
		CancellationToken cancellationToken)
	{
		var owners = type.GetMembers()
			.Where(member => member is IPropertySymbol or IMethodSymbol
			{
				// A constructor wires the whole type together by definition, and an accessor belongs to
				// the property that owns it, which is already in the list.
				MethodKind: not (MethodKind.Constructor or MethodKind.StaticConstructor),
				AssociatedSymbol: null,
			})
			.Where(member => !member.IsImplicitlyDeclared)
			.Where(member => member.DeclaringSyntaxReferences.Length > 0)
			.ToArray();

		if (owners.Length == 0) return Nothing(type, members: 0);

		var touches = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
		var spans = new Dictionary<ISymbol, List<(int Start, int End)>>(SymbolEqualityComparer.Default);

		foreach (var owner in owners)
		{
			touches[owner] = await TouchedAsync(owner, type, solution, cancellationToken);
			spans[owner] = await SpansAsync(owner, cancellationToken);
		}

		var (groups, shared) = ByState(owners, touches);

		// Both kinds in one list, largest first, because the caller is choosing where to cut and the
		// size of the piece matters more than which reading found it.
		IReadOnlyList<Island> islands = Nested(
		[
			.. groups
				.Select(members => Describe(members, touches, shared, spans))
				.Concat(ByReach(owners, touches, shared, spans))
				.OrderByDescending(island => island.Members.Count)
				.ThenBy(island => island.Members.Count > 0 ? island.Members[0] : string.Empty, StringComparer.Ordinal),
		]);

		return new TypeIslands
		{
			Name = type.Name,
			Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } containing
				? containing.ToDisplayString()
				: null,
			Members = owners.Length,
			Islands = islands,
			Spine = [.. shared.Select(field => field.Name).Order(StringComparer.Ordinal)],
		};
	}

	/// <summary>
	/// Each island told which one it sits inside, where one does. Dominance nests by construction --
	/// what a member owns contains what the members under it own -- so several of these lists are
	/// subsets of each other, and saying nothing about that leaves the reader to work out whether
	/// they are alternatives or layers. The smallest container is named rather than every one, since
	/// naming the chain repeats what following it would show.
	/// </summary>
	private static IReadOnlyList<Island> Nested(IReadOnlyList<Island> islands) =>
	[
		.. islands.Select(island =>
		{
			var inside = islands
				.Where(other => other.Owner is not null
					&& other.Members.Count > island.Members.Count
					&& island.Members.All(other.Members.Contains))
				.OrderBy(other => other.Members.Count)
				.FirstOrDefault();

			return inside is null ? island : island with { Within = inside.Owner };
		}),
	];

	/// <summary>A type with nothing to say about it, named so the caller knows it was looked at.</summary>
	private static TypeIslands Nothing(INamedTypeSymbol type, int members) => new()
	{
		Name = type.Name,
		Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } containing
			? containing.ToDisplayString()
			: null,
		Members = members,
		Islands = [],
	};

	/// <summary>
	/// Members joined where they read the same field, and the fields read too widely to join anything.
	/// <para>
	/// Deliberately blind to calls. A call is the strongest edge in this graph and the least
	/// informative: a guard every verb runs first, or a formatter everything ends with, welds the type
	/// into one piece and hides exactly the groups being looked for. What two members share by sharing
	/// a field is the thing that would have to move with them.
	/// </para>
	/// </summary>
	private static (List<List<ISymbol>> Groups, HashSet<ISymbol> Shared) ByState(
		IReadOnlyList<ISymbol> owners,
		Dictionary<ISymbol, HashSet<ISymbol>> touches)
	{
		var readers = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

		foreach (var owner in owners)
		{
			foreach (var field in touches[owner].OfType<IFieldSymbol>())
			{
				if (!readers.TryGetValue(field, out var list)) readers[field] = list = [];
				list.Add(owner);
			}
		}

		// Measured against the members that read any field at all, so a type whose members mostly
		// stand alone does not make every field it does have look shared by comparison.
		var stateful = owners.Count(owner => touches[owner].OfType<IFieldSymbol>().Any());
		var floor = Math.Max(2, (int)Math.Ceiling(stateful * SpineShare));

		var shared = new HashSet<ISymbol>(
			readers.Where(pair => pair.Value.Count >= floor).Select(pair => pair.Key),
			SymbolEqualityComparer.Default);

		var parent = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
		foreach (var owner in owners) parent[owner] = owner;

		ISymbol Find(ISymbol symbol)
		{
			while (!SymbolEqualityComparer.Default.Equals(parent[symbol], symbol)) symbol = parent[symbol];
			return symbol;
		}

		foreach (var (field, list) in readers)
		{
			if (shared.Contains(field)) continue;

			for (var i = 1; i < list.Count; i++)
			{
				var (a, b) = (Find(list[0]), Find(list[i]));
				if (!SymbolEqualityComparer.Default.Equals(a, b)) parent[a] = b;
			}
		}

		var grouped = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

		foreach (var owner in owners)
		{
			// A member reading no field of its own is not evidence of anything, and is left out rather
			// than reported as a group of one.
			if (!touches[owner].OfType<IFieldSymbol>().Any(field => !shared.Contains(field))) continue;

			var root = Find(owner);
			if (!grouped.TryGetValue(root, out var members)) grouped[root] = members = [];
			members.Add(owner);
		}

		var groups = grouped.Values
			.Where(members => members.Count > 1)
			.OrderByDescending(members => members.Count)
			.ToList();

		return (groups, shared);
	}

	/// <summary>
	/// For each member, the members every path to which runs through it -- its own private world.
	/// <para>
	/// This is dominance over the call graph, and it is the question a partition cannot answer. The
	/// enum helpers in a member-editing service were connected to everything, because they called the
	/// same indentation and line-ending helpers the rest of the file called. What made them a unit was
	/// that nothing reached <em>them</em> except one method.
	/// </para>
	/// <para>
	/// Entered from every member nothing else calls and every member visible outside the type, so a
	/// surface with several doors is not mistaken for one with a single hall.
	/// </para>
	/// </summary>
	private static IReadOnlyList<Island> ByReach(
		IReadOnlyList<ISymbol> owners,
		Dictionary<ISymbol, HashSet<ISymbol>> touches,
		HashSet<ISymbol> shared,
		Dictionary<ISymbol, List<(int Start, int End)>> spans)
	{
		var index = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
		for (var i = 0; i < owners.Count; i++) index[owners[i]] = i;

		var calls = new List<int>[owners.Count];
		var callers = new List<int>[owners.Count];

		for (var i = 0; i < owners.Count; i++)
		{
			calls[i] = [];
			callers[i] = [];
		}

		for (var i = 0; i < owners.Count; i++)
		{
			foreach (var target in touches[owners[i]])
			{
				if (target is IFieldSymbol) continue;
				if (!index.TryGetValue(target, out var j) || j == i) continue;

				calls[i].Add(j);
				callers[j].Add(i);
			}
		}

		var entries = Enumerable.Range(0, owners.Count)
			.Where(i => callers[i].Count == 0 || owners[i].DeclaredAccessibility != Accessibility.Private)
			.ToHashSet();

		var dominators = Dominators(owners.Count, entries, calls, callers);

		var sizes = new int[owners.Count];
		for (var i = 0; i < owners.Count; i++)
		{
			sizes[i] = dominators.Count(set => set?.Contains(i) == true);
		}

		var owned = new List<Island>();

		for (var i = 0; i < owners.Count; i++)
		{
			if (sizes[i] < OwnedFloor) continue;
			if (sizes[i] >= owners.Count * OwnedCeiling) continue;

			// A member owning exactly what its only caller owns is a link in a chain, not a world of
			// its own: the caller is the one worth naming.
			if (callers[i].Count == 1 && sizes[callers[i][0]] == sizes[i]) continue;

			var subtree = Enumerable.Range(0, owners.Count)
				.Where(j => dominators[j]?.Contains(i) == true)
				.ToArray();

			owned.Add(new Island
			{
				Kind = "reach",
				Owner = owners[i].Name,
				Members = [.. subtree.Select(j => owners[j].Name).Order(StringComparer.Ordinal)],
				Fields = Own(subtree.Select(j => owners[j]), touches, shared),
				Spans = Merged([.. subtree.SelectMany(j => spans[owners[j]])]),
			});
		}

		// A member whose world is another's minus itself is a door into that world, not a world: the
		// two would move together and naming both says one thing twice. A world genuinely smaller than
		// the one around it stays, because that is the tighter extraction and usually the better one.
		var distinct = owned
			.Where(entry => !owned.Any(other =>
				other.Members.Count > entry.Members.Count
					&& other.Members.Count - entry.Members.Count <= 1
					&& entry.Members.All(other.Members.Contains)))
			.ToList();

		return [.. distinct.OrderByDescending(entry => entry.Members.Count).Take(OwnedListed)];
	}

	/// <summary>
	/// Which members every path to each member runs through, by fixpoint: each set shrinks until it
	/// stops moving. The graphs are one type's members, so the simple form is the right one.
	/// </summary>
	private static HashSet<int>?[] Dominators(int count, HashSet<int> entries, List<int>[] calls, List<int>[] callers)
	{
		var everything = Enumerable.Range(0, count).ToHashSet();
		var dominators = new HashSet<int>?[count];

		var reachable = new HashSet<int>();
		var pending = new Queue<int>(entries);

		while (pending.Count > 0)
		{
			var node = pending.Dequeue();
			if (!reachable.Add(node)) continue;

			foreach (var next in calls[node]) pending.Enqueue(next);
		}

		foreach (var node in reachable) dominators[node] = entries.Contains(node) ? [node] : [.. everything];

		var settling = true;

		while (settling)
		{
			settling = false;

			foreach (var node in reachable)
			{
				if (entries.Contains(node)) continue;

				HashSet<int>? through = null;

				foreach (var caller in callers[node])
				{
					if (!reachable.Contains(caller)) continue;

					through = through is null
						? [.. dominators[caller]!]
						: [.. through.Intersect(dominators[caller]!)];
				}

				through ??= [];
				through.Add(node);

				if (through.SetEquals(dominators[node]!)) continue;

				dominators[node] = through;
				settling = true;
			}
		}

		return dominators;
	}

	/// <summary>
	/// What one member touches inside its own type: the fields it reads or writes and the members it
	/// calls. Resolved through the semantic model rather than matched by name, so a local called
	/// <c>target</c> is not mistaken for a field called <c>_target</c>, and a call is attributed to
	/// the overload it actually reaches.
	/// </summary>
	private static async Task<HashSet<ISymbol>> TouchedAsync(
		ISymbol owner,
		INamedTypeSymbol type,
		Solution solution,
		CancellationToken cancellationToken)
	{
		var touched = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

		foreach (var reference in owner.DeclaringSyntaxReferences)
		{
			var node = await reference.GetSyntaxAsync(cancellationToken);
			var document = solution.GetDocument(node.SyntaxTree);
			if (document is null) continue;

			var model = await document.GetSemanticModelAsync(cancellationToken);
			if (model is null) continue;

			foreach (var descendant in node.DescendantNodes())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var symbol = model.GetSymbolInfo(descendant, cancellationToken).Symbol?.OriginalDefinition;

				// An accessor stands for the property, so reading one is reading the other and the
				// graph does not gain a node nobody wrote.
				if (symbol is IMethodSymbol { AssociatedSymbol: { } associated }) symbol = associated;

				if (symbol is not IFieldSymbol and not IMethodSymbol and not IPropertySymbol) continue;
				if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type)) continue;
				if (SymbolEqualityComparer.Default.Equals(symbol, owner)) continue;

				touched.Add(symbol);
			}
		}

		return touched;
	}

	private static async Task<List<(int Start, int End)>> SpansAsync(ISymbol owner, CancellationToken cancellationToken)
	{
		var spans = new List<(int Start, int End)>();

		foreach (var reference in owner.DeclaringSyntaxReferences)
		{
			var node = await reference.GetSyntaxAsync(cancellationToken);
			var span = node.SyntaxTree.GetLineSpan(node.Span);

			spans.Add((span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
		}

		return spans;
	}

	private static Island Describe(
		List<ISymbol> members,
		Dictionary<ISymbol, HashSet<ISymbol>> touches,
		HashSet<ISymbol> shared,
		Dictionary<ISymbol, List<(int Start, int End)>> spans) => new()
		{
			Kind = "state",
			Members = [.. members.Select(member => member.Name).Order(StringComparer.Ordinal)],
			Fields = Own(members, touches, shared),
			Spans = Merged([.. members.SelectMany(member => spans[member])]),
		};

	/// <summary>
	/// The fields a set of members touches that the spine does not already claim -- the state the
	/// island would take with it, and nothing the rest of the type is still reading.
	/// </summary>
	private static IReadOnlyList<string> Own(
		IEnumerable<ISymbol> members,
		Dictionary<ISymbol, HashSet<ISymbol>> touches,
		HashSet<ISymbol> shared) =>
		[.. members
			.SelectMany(member => touches[member].OfType<IFieldSymbol>())
			.Where(field => !shared.Contains(field))
			.Select(field => field.Name)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)];

	/// <summary>
	/// The spans a set of members occupies, with anything separated by a couple of lines run
	/// together, so neighbours read as the one block they are.
	/// </summary>
	private static IReadOnlyList<string> Merged(List<(int Start, int End)> lines)
	{
		if (lines.Count == 0) return [];

		lines.Sort();

		var merged = new List<(int Start, int End)> { lines[0] };

		foreach (var (start, end) in lines.Skip(1))
		{
			var last = merged[^1];

			if (start <= last.End + 3)
			{
				merged[^1] = (last.Start, Math.Max(last.End, end));
				continue;
			}

			merged.Add((start, end));
		}

		return [.. merged.Select(span => $"{span.Start}-{span.End}")];
	}
}
