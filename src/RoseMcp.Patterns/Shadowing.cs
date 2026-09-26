using Microsoft.CodeAnalysis;

namespace RoseMcp.Patterns;

/// <summary>
/// Refuses a rule that an earlier rule matches everything it does, since it could never win a site.
/// <para>
/// This is not tidiness. Two overloads can share a shape with their roles reversed --
/// <c>Contains(collection, filter)</c> and <c>Contains(expected, collection)</c> both take two
/// arguments -- so an unnamed <c>Assert.Contains($xs$, $p$)</c> listed first captures every item check
/// with the collection and the item swapped, and the replacement compiles. The later rule, the one that
/// was meant for those sites, then never matches anything, and that is the only sign of it. Refusing it
/// turns a silent swap into a message naming both rules.
/// </para>
/// <para>
/// The check is conservative: it says an earlier rule covers a later one only when that is certain, so
/// a partial overlap -- which is what ordering a specific rule before a general one is for -- is left
/// alone.
/// </para>
/// </summary>
internal static class Shadowing
{
	/// <summary>Throws for the first rule in <paramref name="bound"/> that an earlier one covers.</summary>
	public static void Refuse(IReadOnlyList<BoundRule> bound)
	{
		for (var later = 1; later < bound.Count; later++)
		{
			for (var earlier = 0; earlier < later; earlier++)
			{
				var first = bound[earlier];
				var second = bound[later];

				// A statement rule only ever matches a call that is a statement, so it cannot cover an
				// expression rule that also matches calls used as values.
				var comparable = !first.Rule.Find.IsStatement || second.Rule.Find.IsStatement;

				if (!comparable || !Covers(first.Root, second.Root)) continue;

				var overloads = string.Join(", ", second.Methods.Select(method => $"`{method}`"));

				throw new PatternException(
					$"Rule {second.Rule.Number} can never match: rule {first.Rule.Number} comes first and matches every "
					+ $"call it does, on {overloads}. Put rule {second.Rule.Number} first, or narrow rule "
					+ $"{first.Rule.Number} -- give a placeholder a type, as $s:string$, or name the parameter an "
					+ "argument is for, as filter: $p$, which picks the overloads that have a parameter of that name.");
			}
		}
	}

	/// <summary>Whether everything <paramref name="later"/> matches, <paramref name="earlier"/> matches too.</summary>
	private static bool Covers(PatternNode earlier, PatternNode later) => (earlier, later) switch
	{
		(PlaceholderNode { Constraint: null }, _) => true,
		(PlaceholderNode { Constraint: { } wide }, PlaceholderNode { Constraint: { } narrow }) =>
			SymbolEqualityComparer.Default.Equals(wide, narrow),
		(ConstantNode first, ConstantNode second) =>
			Equals(first.Value, second.Value) && SymbolEqualityComparer.Default.Equals(first.Type, second.Type),
		(MemberNode first, MemberNode second) => SymbolEqualityComparer.Default.Equals(first.Member, second.Member),
		(NotNode first, NotNode second) => Covers(first.Operand, second.Operand),
		(LambdaNode, LambdaNode) => true,
		(InvocationNode first, InvocationNode second) => second.Candidates.All(
			candidate => first.Candidates.Any(wider => Covers(wider, candidate))),
		_ => false,
	};

	/// <summary>Whether one overload of an earlier call matches everything one overload of a later call does.</summary>
	private static bool Covers(MethodCandidate earlier, MethodCandidate later)
	{
		if (!SymbolEqualityComparer.Default.Equals(earlier.Method, later.Method)) return false;
		if (!CoversTypeArguments(earlier.TypeArguments, later.TypeArguments)) return false;

		var instanceCovered = (earlier.Instance, later.Instance) switch
		{
			(null, null) => true,
			({ } first, { } second) => Covers(first, second),
			_ => false,
		};

		if (!instanceCovered) return false;

		// A parameter one pattern leaves to its default and the other fills is not covered either way:
		// leaving it out requires the default, and filling it requires an argument.
		foreach (var parameter in earlier.Method.Parameters)
		{
			var inFirst = earlier.Arguments.TryGetValue(parameter.Ordinal, out var first);
			var inSecond = later.Arguments.TryGetValue(parameter.Ordinal, out var second);

			if (inFirst != inSecond) return false;
			if (inFirst && !Covers(first!, second!)) return false;
		}

		return true;
	}

	/// <summary>Whether the type arguments an earlier call writes match every type argument list a later one does.</summary>
	private static bool CoversTypeArguments(IReadOnlyList<TypeArgumentNode>? earlier, IReadOnlyList<TypeArgumentNode>? later)
	{
		if (earlier is null || later is null) return earlier is null && later is null;

		return earlier.Zip(later).All(pair => pair switch
		{
			(TypePlaceholderNode, _) => true,
			(ConcreteTypeNode first, ConcreteTypeNode second) => SymbolEqualityComparer.Default.Equals(first.Type, second.Type),
			_ => false,
		});
	}
}
