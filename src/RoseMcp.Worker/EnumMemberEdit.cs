using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// Adds values to an enum, whose members are items in a comma-separated list rather than
/// declarations standing on their own.
/// <para>
/// Separate from the rest of member editing because that difference reaches everything. The list is
/// rebuilt rather than spliced, because a comma belongs to neither the item before it nor the one
/// after. Every item keeps the separator it had, an item that had none gains one, and the last goes
/// with or without according to whether the enum already ended in a trailing comma. The line break
/// that followed an item moves onto the comma it gains, or the comma lands at the start of the next
/// line.
/// </para>
/// <para>
/// The enum's own layout decides whether values are separated by blank lines, since values written
/// one to a line and values each under their own documentation are both ordinary, and a blank line
/// dropped into the first kind is a diff line nobody asked for.
/// </para>
/// <para>
/// And an enum is the one container where adding a member changes what the members already there
/// mean, which is why this is the only kind of addition that reads the result back and reports on
/// it.
/// </para>
/// </summary>
internal static class EnumMemberEdit
{
	/// <summary>
	/// Adds the supplied values to <paramref name="enum"/>, in the place
	/// <paramref name="request"/> asks for, and says what that did to the values already there.
	/// </summary>
	internal static async Task<MemberEditService.Written> AddAsync(
		TypeTarget target,
		EnumDeclarationSyntax @enum,
		MemberEditRequest request,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var document = target.Document;

		var text = await document.GetTextAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken)
			?? throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");

		var rules = Whitespace.RulesFor(document.Project, tree, text);
		var lineEnding = rules.LineEnding;

		var indent = @enum.Members.Count > 0
			? MemberEditService.IndentAt(text, @enum.Members[0].SpanStart)
			: MemberEditService.IndentAt(text, @enum.SpanStart) + rules.IndentUnit;

		var parsed = MemberSyntax.Parse(
			request.Code,
			MemberSyntax.KeywordOf(@enum),
			document.Project.ParseOptions,
			indent,
			lineEnding,
			count => notices.Add(MemberEditService.RewrittenEndings(count, text)),
			count => notices.Add(MemberSyntax.ReindentedLiteral(count)));

		var adding = parsed.Cast<EnumMemberDeclarationSyntax>().Select(WithSeparatorTrivia).ToArray();

		GuardDuplicates(@enum, adding);

		var index = PlacementIndex(@enum, request);

		var separated = @enum.Members.Skip(1).Any(MemberEditService.StartsBlank);
		var followerIsSeparated = index >= @enum.Members.Count || MemberEditService.StartsBlank(@enum.Members[index]);

		var marker = new SyntaxAnnotation();

		var prepared = adding.Select((member, position) => (EnumMemberDeclarationSyntax)MemberSyntax.Prepared(
			member,
			blankBefore: separated && (position > 0 || index > 0),
			blankAfter: separated && position == adding.Length - 1 && !followerIsSeparated,
			lineEnding,
			indent,
			marker));

		var root = await MemberEditService.RootOf(document, cancellationToken);
		var edited = root.ReplaceNode(@enum, @enum.WithMembers(Separated(@enum.Members, index, prepared)));

		await NoteValuesAsync(target, edited, marker, notices, cancellationToken);

		return new MemberEditService.Written(
			document,
			edited,
			marker,
			target.Signature,
			[.. adding.Select(member => member.Identifier.Text)],
			target.Symbol);
	}

	/// <summary>
	/// The member carrying whatever followed its comma in the code it was parsed from, which is where a
	/// comment written on the same line as the value lives -- and taking the member without its comma
	/// would drop that comment without trace.
	/// </summary>
	private static EnumMemberDeclarationSyntax WithSeparatorTrivia(EnumMemberDeclarationSyntax member)
	{
		if (member.Parent is not EnumDeclarationSyntax wrapper) return member;

		var index = wrapper.Members.IndexOf(member);
		if (index < 0 || index >= wrapper.Members.SeparatorCount) return member;

		var separator = wrapper.Members.GetSeparator(index);

		return member.WithTrailingTrivia(
			member.GetTrailingTrivia().AddRange(separator.LeadingTrivia).AddRange(separator.TrailingTrivia));
	}

	/// <summary>
	/// The enum's items with <paramref name="adding"/> inserted at <paramref name="index"/>, every
	/// separator in place and the trailing comma kept exactly as the enum had it.
	/// </summary>
	private static SeparatedSyntaxList<EnumMemberDeclarationSyntax> Separated(
		SeparatedSyntaxList<EnumMemberDeclarationSyntax> members,
		int index,
		IEnumerable<EnumMemberDeclarationSyntax> adding)
	{
		var hadTrailingComma = members.Count > 0 && members.SeparatorCount == members.Count;

		var items = members
			.Select((member, position) => (Member: member, Separator: position < members.SeparatorCount
				? members.GetSeparator(position)
				: (SyntaxToken?)null))
			.ToList();

		items.InsertRange(index, adding.Select(member => (Member: member, Separator: (SyntaxToken?)null)));

		var nodes = new List<SyntaxNodeOrToken>(items.Count * 2);

		for (var position = 0; position < items.Count; position++)
		{
			var (member, separator) = items[position];
			var isUnseparatedLast = position == items.Count - 1 && !hadTrailingComma;

			if (isUnseparatedLast)
			{
				nodes.Add(member);
			}
			else if (separator is { } kept)
			{
				nodes.Add(member);
				nodes.Add(kept);
			}
			else
			{
				// The line break after the item is its trailing trivia, so the comma takes it over or
				// would be written at the start of the following line.
				nodes.Add(member.WithTrailingTrivia());
				nodes.Add(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(member.GetTrailingTrivia()));
			}
		}

		return SyntaxFactory.SeparatedList<EnumMemberDeclarationSyntax>(nodes);
	}

	/// <summary>Refuses a value by a name the enum already has, or one the code declares twice.</summary>
	private static void GuardDuplicates(EnumDeclarationSyntax @enum, IReadOnlyList<EnumMemberDeclarationSyntax> adding)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var member in adding)
		{
			var name = member.Identifier.Text;
			var clash = @enum.Members.FirstOrDefault(existing => existing.Identifier.Text == name);

			if (clash is not null)
			{
				throw new ArgumentException(
					$"{@enum.Identifier.Text} already declares {name}, at line {MemberEditService.LineOf(clash)}. Adding another would be "
						+ "a duplicate the compiler rejects; rose_replace_member writes over the one that is there.");
			}

			if (!seen.Add(name))
			{
				throw new ArgumentException($"The code declares {name} twice, and an enum has one value by each name.");
			}
		}
	}

	private static int PlacementIndex(EnumDeclarationSyntax @enum, MemberEditRequest request)
	{
		var anchor = request.After is { Length: > 0 } after ? after : request.Before;
		if (anchor is not { Length: > 0 }) return @enum.Members.Count;

		var found = @enum.Members.IndexOf(member => member.Identifier.Text == anchor);

		if (found < 0)
		{
			var declared = @enum.Members.Select(member => member.Identifier.Text).ToArray();

			throw new ArgumentException(
				$"{@enum.Identifier.Text} declares no value called '{anchor}' to put this next to."
					+ (declared.Length == 0
						? " It declares no values at all, so leave after and before out."
						: $" It declares: {string.Join(", ", declared)}."));
		}

		return request.After is { Length: > 0 } ? found + 1 : found;
	}

	/// <summary>
	/// Says when an addition changes what an existing value is, or gives a new one a value another
	/// already has.
	/// <para>
	/// Asked of a compilation of the result rather than of the text, because an item without an
	/// initialiser is one more than the item above it, and an initialiser can be any constant expression
	/// over the others in whatever underlying type the enum declares. Renumbering compiles cleanly and
	/// changes what every stored or serialised value means, which has no symptom until data is read
	/// back -- so it is said, with the numbers.
	/// </para>
	/// <para>
	/// Said rather than refused, because both are sometimes exactly what was meant: an enum nothing has
	/// persisted can be renumbered freely, and a [Flags] enum routinely gives one value two names. A
	/// refusal with no way to insist teaches a caller to route around the tool, back to the text edit it
	/// replaces. A collision whose initialiser names the value it equals is an alias written on purpose
	/// and is not mentioned. A value the underlying type cannot hold is left to the compile afterwards,
	/// which reports it as the error it is.
	/// </para>
	/// </summary>
	private static async Task NoteValuesAsync(
		TypeTarget target,
		SyntaxNode edited,
		SyntaxAnnotation marker,
		List<string> notices,
		CancellationToken cancellationToken)
	{
		var before = target.Symbol.GetMembers()
			.OfType<IFieldSymbol>()
			.Where(field => field.HasConstantValue)
			.ToDictionary(field => field.Name, field => field.ConstantValue, StringComparer.Ordinal);

		var document = target.Document.WithSyntaxRoot(edited);
		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var model = await document.GetSemanticModelAsync(cancellationToken);

		if (root is null || model is null) return;
		if (root.GetAnnotatedNodes(marker).FirstOrDefault()?.Parent is not EnumDeclarationSyntax @enum) return;

		var values = @enum.Members
			.Select(member => (Member: member, Symbol: model.GetDeclaredSymbol(member, cancellationToken)))
			.Where(value => value.Symbol is { HasConstantValue: true })
			.ToArray();

		var added = values.Where(value => value.Member.HasAnnotation(marker)).ToArray();

		var renumbered = values
			.Where(value => before.TryGetValue(value.Symbol!.Name, out var old) && !Equals(old, value.Symbol.ConstantValue))
			.Select(value => $"{value.Symbol!.Name} from {before[value.Symbol.Name]} to {value.Symbol.ConstantValue}")
			.ToArray();

		if (renumbered.Length > 0)
		{
			notices.Add(
				$"Adding {string.Join(", ", added.Select(value => value.Symbol!.Name))} there renumbers "
					+ $"{string.Join(", ", renumbered)}, because a value without an initialiser is one more than the "
					+ $"value above it. That compiles, and changes what every stored or serialised {@enum.Identifier.Text} "
					+ "means. If anything has kept these values, give the new ones explicit initialisers or add them "
					+ "after the last value.");
		}

		foreach (var (member, symbol) in added)
		{
			var clashes = values
				.Where(other => !ReferenceEquals(other.Member, member) && Equals(other.Symbol!.ConstantValue, symbol!.ConstantValue))
				.Select(other => other.Symbol!)
				.ToArray();

			if (clashes.Length == 0) continue;

			var names = member.EqualsValue?.Value
				.DescendantNodesAndSelf()
				.OfType<IdentifierNameSyntax>()
				.Select(identifier => model.GetSymbolInfo(identifier, cancellationToken).Symbol)
				.ToArray() ?? [];

			var isAlias = clashes.Any(clash => names.Contains(clash, SymbolEqualityComparer.Default));
			if (isAlias) continue;

			notices.Add(
				$"{symbol!.Name} is {symbol.ConstantValue}, which {string.Join(" and ", clashes.Select(clash => clash.Name))} "
					+ $"already {(clashes.Length == 1 ? "is" : "are")}, so the two cannot be told apart at run time. If it "
					+ $"is meant to be an alias, writing the initialiser as {clashes[0].Name} says so.");
		}
	}
}
