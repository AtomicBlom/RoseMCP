using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Which of a type's members belong together, worked out from what they touch rather than from
/// where they were written.
/// <para>
/// A type's members form a graph: two are joined when they read the same field, and when one calls
/// the other. Where that graph comes apart into pieces, the pieces are doing separate jobs that
/// happen to share a file, and each is a candidate for a type of its own. Where it does not come
/// apart, the type holds together and the answer is one group.
/// </para>
/// <para>
/// The line ranges are half the value and the reason this is worth a tool rather than a metric. A
/// group written in one block is an afternoon's work to lift out; the same group scattered over
/// three ranges a thousand lines apart is a different job entirely, and nothing about a member list
/// says which one is in front of you.
/// </para>
/// <para>
/// One field usually holds the whole type together -- the thing the type <em>is</em>, which nearly
/// every member touches. Counting it would join every member to every other and report one group
/// for everything, so a field that most members touch is named separately and left out of the
/// joining. That is not a workaround: a type's identity is exactly the state that is meant to be
/// shared, and the question here is what is shared by less than all of it.
/// </para>
/// </summary>
internal static class Cohesion
{
	/// <summary>
	/// The share of members that has to touch a field before it counts as the type's own state
	/// rather than one group's. Half, because a field two groups of five use tells you the groups
	/// are joined, and a field nine of ten use tells you nothing at all.
	/// </summary>
	private const double SpineShare = 0.3;

	/// <summary>
	/// The type's members grouped by what they touch, with the fields each group has to itself and
	/// the spans it occupies, or a single group where the type holds together.
	/// </summary>
	internal static async Task<TypeCohesion> OfAsync(
		INamedTypeSymbol type,
		Solution solution,
		CancellationToken cancellationToken)
	{
		var state = type.GetMembers().OfType<IFieldSymbol>()
			.Where(field => !field.IsImplicitlyDeclared && !field.IsConst)
			.ToArray();

		var owners = type.GetMembers()
			.Where(member => member is IPropertySymbol or IMethodSymbol
			{
				// A constructor wires the whole type together by definition, and an accessor belongs
				// to the property that owns it, which is already in the list.
				MethodKind: not (MethodKind.Constructor or MethodKind.StaticConstructor),
				AssociatedSymbol: null,
			})
			.Where(member => !member.IsImplicitlyDeclared)
			.Where(member => member.DeclaringSyntaxReferences.Length > 0)
			.ToArray();

		if (owners.Length == 0) return new TypeCohesion { Groups = [], Shared = [] };

		var touches = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);

		foreach (var owner in owners)
		{
			touches[owner] = await TouchedAsync(owner, type, solution, cancellationToken);
		}

		// Anything most of the type touches is its own plumbing rather than one group's business, and
		// that is as true of a helper as of a field: a guard every verb calls first joins every verb
		// to every other exactly the way a shared field does. Counting both and leaving both out is
		// what stops the answer being "one group, all of it".
		var floor = Math.Max(3, (int)Math.Ceiling(owners.Length * SpineShare));

		int Users(ISymbol symbol) =>
			touches.Values.Count(set => set.Contains(symbol, SymbolEqualityComparer.Default));

		var spine = state.Where(field => Users(field) >= floor).ToArray<ISymbol>();
		var plumbing = owners.Where(member => Users(member) >= floor).ToArray();

		var ignored = new HashSet<ISymbol>(spine.Concat(plumbing), SymbolEqualityComparer.Default);

		var groups = Partition(owners, touches, ignored);

		return new TypeCohesion
		{
			Groups = await DescribeAsync(groups, touches, ignored, solution, cancellationToken),
			Shared = [.. ignored.Select(symbol => symbol.Name).Order(StringComparer.Ordinal)],
		};
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

				if (symbol is null) continue;
				if (symbol is not IFieldSymbol and not IMethodSymbol and not IPropertySymbol) continue;
				if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type)) continue;
				if (SymbolEqualityComparer.Default.Equals(symbol, owner)) continue;

				touched.Add(symbol);
			}
		}

		return touched;
	}

	/// <summary>
	/// The members joined into groups: same field, or one calling the other. Union-find over the
	/// members, because the question is which pieces the graph falls into and not what the path
	/// between any two of them is.
	/// </summary>
	private static List<List<ISymbol>> Partition(
		IReadOnlyList<ISymbol> owners,
		Dictionary<ISymbol, HashSet<ISymbol>> touches,
		IReadOnlySet<ISymbol> ignored)
	{
		var parent = new Dictionary<ISymbol, ISymbol>(SymbolEqualityComparer.Default);
		foreach (var owner in owners) parent[owner] = owner;

		ISymbol Find(ISymbol symbol)
		{
			while (!SymbolEqualityComparer.Default.Equals(parent[symbol], symbol)) symbol = parent[symbol];
			return symbol;
		}

		void Union(ISymbol left, ISymbol right)
		{
			var (a, b) = (Find(left), Find(right));
			if (!SymbolEqualityComparer.Default.Equals(a, b)) parent[a] = b;
		}

		var byField = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

		foreach (var owner in owners)
		{
			foreach (var target in touches[owner])
			{
				if (target is IFieldSymbol field)
				{
					if (ignored.Contains(field)) continue;

					if (!byField.TryGetValue(field, out var sharers)) byField[field] = sharers = [];
					sharers.Add(owner);
					continue;
				}

				// A call joins the two directly. This is what finds a group in a type with no state
				// at all, where the only thing holding a set of helpers together is that one entry
				// point reaches them and nothing else does.
				if (parent.ContainsKey(target) && !ignored.Contains(target)) Union(owner, target);
			}
		}

		foreach (var sharers in byField.Values)
		{
			for (var i = 1; i < sharers.Count; i++) Union(sharers[0], sharers[i]);
		}

		var grouped = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

		foreach (var owner in owners)
		{
			var root = Find(owner);
			if (!grouped.TryGetValue(root, out var members)) grouped[root] = members = [];
			members.Add(owner);
		}

		return [.. grouped.Values.OrderByDescending(members => members.Count)];
	}

	/// <summary>
	/// Each group with the fields only it touches and the spans it occupies. The spans are merged
	/// where they run together, so a group written as one block reports one range and a scattered
	/// one reports what it costs to collect.
	/// </summary>
	private static async Task<IReadOnlyList<MemberGroup>> DescribeAsync(
		List<List<ISymbol>> groups,
		Dictionary<ISymbol, HashSet<ISymbol>> touches,
		IReadOnlySet<ISymbol> ignored,
		Solution solution,
		CancellationToken cancellationToken)
	{
		var described = new List<MemberGroup>();

		foreach (var members in groups)
		{
			var fields = members
				.SelectMany(member => touches[member].OfType<IFieldSymbol>())
				.Where(field => !ignored.Contains(field))
				.Distinct(SymbolEqualityComparer.Default)
				.OfType<IFieldSymbol>()
				.Select(field => field.Name)
				.Order(StringComparer.Ordinal)
				.ToArray();

			var lines = new List<(int Start, int End)>();

			foreach (var member in members)
			{
				foreach (var reference in member.DeclaringSyntaxReferences)
				{
					var node = await reference.GetSyntaxAsync(cancellationToken);
					var span = node.SyntaxTree.GetLineSpan(node.Span);

					lines.Add((span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
				}
			}

			described.Add(new MemberGroup
			{
				Members = [.. members.Select(member => member.Name).Order(StringComparer.Ordinal)],
				Fields = fields,
				Spans = Merged(lines),
			});
		}

		return described;
	}

	/// <summary>
	/// The spans a group occupies, with anything separated by less than a couple of lines run
	/// together, so neighbouring members read as the one block they are.
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
