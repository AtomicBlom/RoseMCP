using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// What changed between the parameters a member has and the ones it is being given.
/// <para>
/// Worked out by matching names, because a name is the only thing about a parameter that a caller
/// can be relied on to keep: a parameter with the same name is the same parameter, moved or retyped,
/// and a name that is new is a new parameter. Renaming one is therefore indistinguishable from
/// removing it and adding another, which is the right answer -- renaming a parameter is
/// rose_rename_symbol's job, and it moves the named arguments at every call site too, which nothing
/// here would know to do.
/// </para>
/// </summary>
public sealed record ParameterPlan
{
	public required IReadOnlyList<PlannedParameter> Parameters { get; init; }

	/// <summary>Parameters the member had and will not have. Named, because their arguments have to go.</summary>
	public required IReadOnlyList<string> Removed { get; init; }

	/// <summary>Parameters that stayed but changed type, which is what can break a call site silently.</summary>
	public required IReadOnlyList<string> Retyped { get; init; }

	/// <summary>New parameters, in the order they now appear.</summary>
	public IEnumerable<PlannedParameter> Added => Parameters.Where(parameter => parameter.WasAt is null);

	/// <summary>
	/// True when nothing about the call sites has to change: no parameter went, none arrived without
	/// a default, and none moved. This is the case worth knowing about, because it is the common one
	/// -- adding an optional flag to an existing method -- and in it every call site is left exactly
	/// as it was.
	/// </summary>
	public bool CallSitesUnaffected =>
		Removed.Count == 0
			&& Parameters.All(parameter => parameter.WasAt is null ? parameter.HasDefault : parameter.KeptItsPlace);

	public static ParameterPlan For(
		SeparatedSyntaxList<ParameterSyntax> existing,
		SeparatedSyntaxList<ParameterSyntax> wanted)
	{
		var before = existing.Select((parameter, index) => (Name: parameter.Identifier.Text, Index: index, Node: parameter))
			.ToDictionary(entry => entry.Name, StringComparer.Ordinal);

		var planned = new List<PlannedParameter>(wanted.Count);

		for (var index = 0; index < wanted.Count; index++)
		{
			var parameter = wanted[index];
			var name = parameter.Identifier.Text;
			var was = before.TryGetValue(name, out var old) ? old : default;

			planned.Add(new PlannedParameter
			{
				Declaration = parameter,
				Name = name,
				WasAt = before.ContainsKey(name) ? was.Index : null,
				IsAt = index,
				HasDefault = parameter.Default is not null,
				WasParams = was.Node?.Modifiers.Any(SyntaxKind.ParamsKeyword) ?? false,
			});
		}

		var kept = planned.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

		var removed = existing
			.Select(parameter => parameter.Identifier.Text)
			.Where(name => !kept.Contains(name))
			.ToArray();

		var retyped = planned
			.Where(parameter => parameter.WasAt is { } at
				&& !SameType(existing[at], parameter.Declaration))
			.Select(parameter => parameter.Name)
			.ToArray();

		return new ParameterPlan { Parameters = planned, Removed = removed, Retyped = retyped };
	}

	/// <summary>
	/// The one shape this refuses: existing parameters swapped around each other.
	/// <para>
	/// Refused rather than attempted because a reorder has to rewrite every call site's arguments
	/// into the new order, and an argument's meaning at a call site is not always recoverable from
	/// its position -- a params expansion, a ref argument, an omitted optional in the middle. New
	/// parameters may still be inserted anywhere, which is what people actually ask for; what is
	/// refused is moving the ones that are already there.
	/// </para>
	/// </summary>
	public string? WhyImpossible()
	{
		var order = Parameters.Where(parameter => parameter.WasAt is not null).ToArray();

		for (var index = 1; index < order.Length; index++)
		{
			if (order[index].WasAt > order[index - 1].WasAt) continue;

			return $"'{order[index].Name}' would move in front of '{order[index - 1].Name}', and reordering "
				+ "parameters that already exist is not something this does: the arguments at a call site "
				+ "cannot always be put back in a different order safely. New parameters can go anywhere.";
		}

		return null;
	}

	private static bool SameType(ParameterSyntax existing, ParameterSyntax wanted) =>
		string.Equals(
			existing.Type?.ToString().Replace(" ", string.Empty, StringComparison.Ordinal),
			wanted.Type?.ToString().Replace(" ", string.Empty, StringComparison.Ordinal),
			StringComparison.Ordinal);
}
