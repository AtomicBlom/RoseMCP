using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Adds, replaces or removes one attribute on a declaration, addressed by the attribute's own name.
/// <para>
/// Matching by name and refusing when several match is the whole of the care here. Several
/// attributes of one name is ordinary rather than exceptional -- a theory with four
/// <c>InlineData</c> is the shape of most test files -- so replacing the first would be the
/// wrong-overload failure in a new costume: it compiles, the diff looks like what was asked for,
/// and the case that was meant to change did not.
/// </para>
/// <para>
/// Each attribute goes in a list of its own rather than into a shared bracket. Both spellings are
/// legal and the file decides, but one attribute to a line is what the formatter leaves alone and
/// what a diff of a later change can show.
/// </para>
/// </summary>
public static class AttributeEdit
{
	/// <summary>
	/// The declaration with the edit applied.
	/// </summary>
	/// <param name="declaration">The member or type to change.</param>
	/// <param name="attribute">The attribute as it is written in source, with or without brackets.</param>
	/// <param name="action">Whether to set, add or remove it.</param>
	/// <param name="options">The destination project's parse options.</param>
	/// <param name="indent">The declaration's own indentation, for a list being added.</param>
	/// <param name="lineEnding">The file's line ending, for a list being added.</param>
	/// <param name="notices">Where a consequence worth saying is recorded.</param>
	/// <exception cref="ArgumentException">
	/// The attribute does not parse, or the declaration carries more than one of that name, or
	/// removing one that is not there.
	/// </exception>
	public static MemberDeclarationSyntax Apply(
		MemberDeclarationSyntax declaration,
		string attribute,
		AttributeAction action,
		ParseOptions? options,
		string indent,
		string lineEnding,
		List<string> notices) =>
		Applied(
			declaration,
			declaration.AttributeLists,
			static (node, lists) => node.WithAttributeLists(lists),
			attribute,
			action,
			options,
			indent,
			lineEnding,
			inline: false,
			notices);

	/// <summary>
	/// The same edit on a parameter, which is the one attribute position a declaration name cannot
	/// reach. <c>[FromBody]</c>, <c>[CallerMemberName]</c> and every framework's per-parameter
	/// attribute live there, and the only other way to one is rewriting the whole signature.
	/// <para>
	/// It goes inline rather than on a line of its own: a parameter's attribute sits in front of its
	/// type, and a line break there is layout Roslyn's formatter has no rule about, so it would stay
	/// wherever this put it.
	/// </para>
	/// </summary>
	public static ParameterSyntax Apply(
		ParameterSyntax parameter,
		string attribute,
		AttributeAction action,
		ParseOptions? options,
		List<string> notices) =>
		Applied(
			parameter,
			parameter.AttributeLists,
			static (node, lists) => node.WithAttributeLists(lists),
			attribute,
			action,
			options,
			indent: string.Empty,
			lineEnding: string.Empty,
			inline: true,
			notices);

	/// <summary>
	/// The edit itself, over anything carrying attribute lists. Written once rather than once per node
	/// kind, because every rule here -- one name matching two attributes, a list emptied by a removal,
	/// the trivia a documentation comment sits in -- is the same wherever the attribute is.
	/// </summary>
	private static TNode Applied<TNode>(
		TNode node,
		SyntaxList<AttributeListSyntax> lists,
		Func<TNode, SyntaxList<AttributeListSyntax>, TNode> withLists,
		string attribute,
		AttributeAction action,
		ParseOptions? options,
		string indent,
		string lineEnding,
		bool inline,
		List<string> notices)
		where TNode : SyntaxNode
	{
		var parsed = Parse(attribute, options, indent);
		var name = NameOf(parsed);
		var matching = Matching(lists, name);

		return action switch
		{
			AttributeAction.Remove => Remove(node, lists, name, matching, notices),
			AttributeAction.Add => Add(node, withLists, parsed, matching, name, indent, lineEnding, inline, notices),
			_ => Set(node, withLists, parsed, matching, name, indent, lineEnding, inline, notices),
		};
	}

	/// <summary>
	/// The attribute the caller wrote, parsed inside a list so a malformed argument is refused before
	/// the file is opened rather than landing in it, and re-indented for where it is going.
	/// <para>
	/// The re-indentation is the same rule a written member goes through, and it matters here for the
	/// same reason: an attribute argument list wrapped by hand keeps whatever indentation arrived,
	/// because Roslyn's formatter has no rule about where a wrapped list sits and neither IDE0055 nor
	/// <c>dotnet format</c> has an opinion either. Four <c>InlineData</c> arguments written at column
	/// zero would land at column zero, under a declaration several levels in, and nothing would say so.
	/// </para>
	/// </summary>
	private static AttributeSyntax Parse(string attribute, ParseOptions? options, string indent)
	{
		var text = attribute.Trim();
		if (text.Length == 0) throw new ArgumentException("No attribute was supplied, so there is nothing to write.");

		// Re-indented from what arrived rather than from the trimmed copy: trimming takes the first
		// line's indentation off, which is where the baseline is read from, and the rest would then be
		// measured against nothing and land a level deeper than the declaration.
		var placed = MemberSyntax.Reindented(attribute, indent).Trim();
		var bracketed = placed.StartsWith('[') ? placed : $"[{placed}]";

		// Attached to a throwaway declaration, because an attribute list means nothing on its own and
		// parsing it alone reports errors in terms of a construct the caller never wrote.
		var unit = SyntaxFactory.ParseCompilationUnit($"{bracketed} class __RoseMcpAttributeProbe {{ }}", options: options as CSharpParseOptions);

		var errors = unit.GetDiagnostics()
			.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
			.ToArray();

		if (errors.Length > 0)
		{
			throw new ArgumentException(
				$"'{text}' is not an attribute: {errors[0].GetMessage()} Write it as it appears in source, "
					+ "for example Obsolete(\"use Parse\") or InlineData(1, \"a\").");
		}

		var lists = unit.Members.OfType<TypeDeclarationSyntax>().SelectMany(type => type.AttributeLists).ToArray();
		var attributes = lists.SelectMany(list => list.Attributes).ToArray();

		if (attributes.Length != 1)
		{
			throw new ArgumentException(
				$"'{text}' declares {attributes.Length} attributes and this writes one. Call it once for each.");
		}

		return attributes[0];
	}

	/// <summary>
	/// The attributes already there that this one would replace, matched on the name as written and on
	/// the same name with Attribute on the end -- <c>[Obsolete]</c> and <c>[ObsoleteAttribute]</c> are
	/// the same attribute, and a caller should not have to know which spelling the file used.
	/// </summary>
	private static IReadOnlyList<AttributeSyntax> Matching(SyntaxList<AttributeListSyntax> lists, string name) =>
		[.. lists.SelectMany(list => list.Attributes).Where(existing => Same(NameOf(existing), name))];

	private static TNode Set<TNode>(
		TNode node,
		Func<TNode, SyntaxList<AttributeListSyntax>, TNode> withLists,
		AttributeSyntax parsed,
		IReadOnlyList<AttributeSyntax> matching,
		string name,
		string indent,
		string lineEnding,
		bool inline,
		List<string> notices)
		where TNode : SyntaxNode
	{
		if (matching.Count > 1) throw Several(name, matching, "set");

		if (matching.Count == 0)
		{
			notices.Add($"{name} was not there, so it was added rather than replaced.");

			return Attach(node, withLists, parsed, indent, lineEnding, inline);
		}

		return node.ReplaceNode(matching[0], parsed.WithTriviaFrom(matching[0]));
	}

	private static TNode Add<TNode>(
		TNode node,
		Func<TNode, SyntaxList<AttributeListSyntax>, TNode> withLists,
		AttributeSyntax parsed,
		IReadOnlyList<AttributeSyntax> matching,
		string name,
		string indent,
		string lineEnding,
		bool inline,
		List<string> notices)
		where TNode : SyntaxNode
	{
		if (matching.Count > 0)
		{
			notices.Add(
				$"{name} was already there {matching.Count} time(s); this adds another rather than replacing "
					+ "one. Use set to replace.");
		}

		return Attach(node, withLists, parsed, indent, lineEnding, inline);
	}

	private static TNode Remove<TNode>(
		TNode node,
		SyntaxList<AttributeListSyntax> lists,
		string name,
		IReadOnlyList<AttributeSyntax> matching,
		List<string> notices)
		where TNode : SyntaxNode
	{
		if (matching.Count == 0)
		{
			throw new ArgumentException(
				$"{name} is not there, so there is nothing to remove. What is there: {Listed(lists)}.");
		}

		if (matching.Count > 1) throw Several(name, matching, "remove");

		var list = matching[0].FirstAncestorOrSelf<AttributeListSyntax>()!;

		// A list holding only this attribute goes with it, brackets included: an empty [] does not
		// compile, and leaving one would be a syntax error written by a tool that parses everything.
		if (list.Attributes.Count == 1)
		{
			return node.RemoveNode(list, SyntaxRemoveOptions.KeepNoTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
		}

		notices.Add($"{name} shared a bracket with {list.Attributes.Count - 1} other attribute(s), which stay.");

		return node.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(matching[0])));
	}

	/// <summary>
	/// The attribute in a list of its own, after whatever is already there, carrying the leading trivia
	/// so a documentation comment stays above the attributes rather than below them.
	/// <para>
	/// A member's list ends the line and re-indents for the declaration beneath it. A parameter's is
	/// followed by a single space, because it sits in front of the type on the same line.
	/// </para>
	/// </summary>
	private static TNode Attach<TNode>(
		TNode node,
		Func<TNode, SyntaxList<AttributeListSyntax>, TNode> withLists,
		AttributeSyntax parsed,
		string indent,
		string lineEnding,
		bool inline)
		where TNode : SyntaxNode
	{
		SyntaxTrivia[] trailing = inline
			? [SyntaxFactory.Space]
			: [SyntaxFactory.EndOfLine(lineEnding), SyntaxFactory.Whitespace(indent)];

		var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(parsed))
			.WithTrailingTrivia(trailing);

		var leading = node.GetLeadingTrivia();
		var stripped = node.WithoutLeadingTrivia();

		return withLists(stripped, ListsOf(stripped).Add(list)).WithLeadingTrivia(leading);
	}

	/// <summary>The attribute lists a node carries, for the two kinds this writes to.</summary>
	private static SyntaxList<AttributeListSyntax> ListsOf(SyntaxNode node) => node switch
	{
		MemberDeclarationSyntax member => member.AttributeLists,
		ParameterSyntax parameter => parameter.AttributeLists,
		_ => default,
	};

	private static ArgumentException Several(string name, IReadOnlyList<AttributeSyntax> matching, string action)
	{
		var written = string.Join("; ", matching.Take(8).Select(attribute => attribute.ToString()));

		return new ArgumentException(
			$"This declaration carries {matching.Count} attributes called {name}: {written}. Which one to "
				+ $"{action} is not something a name settles, and picking one would compile. Use action=add "
					+ "for another, or edit the declaration with rose_replace_member.");
	}

	private static string Listed(SyntaxList<AttributeListSyntax> lists)
	{
		var names = lists
			.SelectMany(list => list.Attributes)
			.Select(NameOf)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		return names.Length == 0 ? "no attributes" : string.Join(", ", names);
	}

	private static string NameOf(AttributeSyntax attribute) => attribute.Name.ToString();

	/// <summary>
	/// Whether two attribute names are the same one. The <c>Attribute</c> suffix is optional in
	/// source and a qualified name is the same attribute as a bare one.
	/// </summary>
	private static bool Same(string left, string right) => Trimmed(left) == Trimmed(right);

	private static string Trimmed(string name)
	{
		var last = name.Split('.')[^1];

		return last.EndsWith("Attribute", StringComparison.Ordinal) && last.Length > "Attribute".Length
			? last[..^"Attribute".Length]
			: last;
	}
}
