using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// What a type or a file declares, listed rather than read.
/// <para>
/// One tool with two roots, because the result is the same shape either way and only the starting
/// point differs: a name when the caller knows the type, a path when they have the file. Two tools
/// would be two descriptions saying the same thing and two slots on every client's list.
/// </para>
/// <para>
/// This is the read that comes before an edit, and it had no tool. Asking what a type contains
/// meant reading the file, and reading the file is what puts it in front of the caller -- after
/// which the edit goes through a text tool and none of the rest of this surface is worth reaching
/// for.
/// </para>
/// </summary>
public static partial class OutlineService
{
	public static async Task<OutlineResult> OutlineAsync(
		WorkspaceSnapshot snapshot,
		string? type,
		string? filePath,
		bool includeInherited,
		bool includeDocumentation,
		bool includeSignatures,
		CancellationToken cancellationToken)
	{
		var named = !string.IsNullOrWhiteSpace(type);
		var pathed = !string.IsNullOrWhiteSpace(filePath);

		if (named == pathed)
		{
			throw new ArgumentException(
				named
					? "Name a type or give a file path, not both -- they are two ways of choosing what to outline."
					: "Name a type, as Namespace.Type, or give a file path.");
		}

		var notices = new List<string>(snapshot.Notices);

		var detail = new OutlineDetail(includeDocumentation, includeSignatures);

		var types = named
			? [await OfTypeAsync(snapshot, type!, filePath, includeInherited, detail, cancellationToken)]
			: await OfFileAsync(snapshot, filePath!, includeInherited, detail, cancellationToken);

		if (types.Count == 0) notices.Add("The file declares no types.");

		return new OutlineResult
		{
			Revision = snapshot.Revision,
			Target = (named ? type : filePath)!,
			Types = types,
			Notices = notices,
		};
	}

	/// <summary>
	/// How much of each entry to fill in. Two switches rather than one, because they answer different
	/// questions: documentation is what a member is for, a signature is what it takes and returns, and
	/// a caller looking for a name in a large type wants neither.
	/// <para>
	/// A record rather than two bools threaded through four methods, so a third switch does not mean
	/// another parameter on every one of them.
	/// </para>
	/// </summary>
	private readonly record struct OutlineDetail(bool Documentation, bool Signatures);

	private static async Task<OutlinedType> OfTypeAsync(
		WorkspaceSnapshot snapshot,
		string type,
		string? filePath,
		bool includeInherited,
		OutlineDetail detail,
		CancellationToken cancellationToken)
	{
		var target = await DeclarationLocator.FindTypeAsync(snapshot.Solution, type, filePath, cancellationToken);

		return await DescribeAsync(snapshot, target.Symbol, includeInherited, detail, cancellationToken);
	}

	private static async Task<IReadOnlyList<OutlinedType>> OfFileAsync(
		WorkspaceSnapshot snapshot,
		string filePath,
		bool includeInherited,
		OutlineDetail detail,
		CancellationToken cancellationToken)
	{
		var document = SymbolLocator.RequireDocument(snapshot.Solution, filePath);

		var model = await document.GetSemanticModelAsync(cancellationToken);
		var root = await document.GetSyntaxRootAsync(cancellationToken);

		if (model is null || root is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(document.FilePath)} is not a C# source file.");
		}

		var described = new List<OutlinedType>();

		// Declared order, not alphabetical: a file's own order is what a reader is going to see when
		// they open it, and sorting would make the outline and the file disagree.
		foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol) continue;

			described.Add(await DescribeAsync(snapshot, symbol, includeInherited, detail, cancellationToken));
		}

		return described;
	}

	private static async Task<OutlinedType> DescribeAsync(
		WorkspaceSnapshot snapshot,
		INamedTypeSymbol symbol,
		bool includeInherited,
		OutlineDetail detail,
		CancellationToken cancellationToken)
	{
		var members = new List<OutlinedMember>();

		foreach (var member in Members(symbol, includeInherited))
		{
			cancellationToken.ThrowIfCancellationRequested();

			members.Add(await DescribeMemberAsync(snapshot, member, detail, cancellationToken));
		}

		var bases = new List<string>();

		if (symbol.BaseType is { SpecialType: not SpecialType.System_Object } super)
		{
			bases.Add(super.ToDisplayString(SymbolSignature.Format));
		}

		bases.AddRange(symbol.Interfaces.Select(@interface => @interface.ToDisplayString(SymbolSignature.Format)));

		var declarations = new List<SourceLocation>();

		foreach (var location in symbol.Locations.Where(location => location.IsInSource))
		{
			declarations.Add(await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken));
		}

		return new OutlinedType
		{
			Name = symbol.ToDisplayString(SymbolSignature.Format),
			Kind = Kind(symbol),
			Accessibility = symbol.DeclaredAccessibility.ToString(),
			Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == false
				? symbol.ContainingNamespace.ToDisplayString()
				: null,
			BaseTypes = bases,
			Summary = detail.Documentation ? Summary(symbol, cancellationToken) : null,
			Declarations = declarations,
			Members = members,
		};
	}

	private static async Task<OutlinedMember> DescribeMemberAsync(
		WorkspaceSnapshot snapshot,
		ISymbol member,
		OutlineDetail detail,
		CancellationToken cancellationToken)
	{
		var location = member.Locations.FirstOrDefault(candidate => candidate.IsInSource);

		return new OutlinedMember
		{
			Name = member.Name,
			Signature = detail.Signatures ? member.ToDisplayString(SymbolSignature.Format) : null,
			Kind = member.Kind.ToString(),
			Accessibility = member.DeclaredAccessibility.ToString(),
			IsAbstract = member.IsAbstract,
			IsStatic = member.IsStatic,

			// A generator's member has no file to edit, and saying so here saves a call that would
			// refuse for exactly that reason.
			IsGenerated = member.DeclaringSyntaxReferences.Length > 0
				&& snapshot.Solution.GetDocument(member.DeclaringSyntaxReferences[0].SyntaxTree) is null,
			Summary = detail.Documentation ? Summary(member, cancellationToken) : null,
			Location = location is null
				? null
				: await SymbolLocator.DescribeAsync(snapshot.Solution, location, cancellationToken),
		};
	}

	/// <summary>
	/// The members worth listing. Compiler-written ones are left out -- a property's backing field
	/// and its get and set accessors are not things anybody asks a type what it contains and would
	/// treble the length of the answer.
	/// </summary>
	private static IEnumerable<ISymbol> Members(INamedTypeSymbol symbol, bool includeInherited)
	{
		var own = symbol.GetMembers().Where(member => !member.IsImplicitlyDeclared && member.Kind != SymbolKind.Method
			|| member is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator or MethodKind.Conversion }
				&& !member.IsImplicitlyDeclared);

		if (!includeInherited) return own;

		var inherited = Bases(symbol)
			.SelectMany(super => super.GetMembers())
			.Where(member => !member.IsImplicitlyDeclared && member.DeclaredAccessibility != Accessibility.Private);

		return own.Concat(inherited);
	}

	private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol symbol)
	{
		for (var super = symbol.BaseType; super is { SpecialType: not SpecialType.System_Object }; super = super.BaseType)
		{
			yield return super;
		}
	}

	private static string Kind(INamedTypeSymbol symbol) => symbol.TypeKind switch
	{
		TypeKind.Interface => "interface",
		TypeKind.Struct => symbol.IsRecord ? "record struct" : "struct",
		TypeKind.Enum => "enum",
		TypeKind.Delegate => "delegate",
		_ => symbol.IsRecord ? "record" : "class",
	};

	/// <summary>
	/// The summary text out of the documentation XML, flattened to one line. Flattened because an
	/// outline is a list and a fifteen-line comment in a list is not a list -- the whole comment is
	/// a rose_symbol_info call away for a member that turns out to matter.
	/// </summary>
	private static string? Summary(ISymbol symbol, CancellationToken cancellationToken)
	{
		var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
		if (string.IsNullOrWhiteSpace(xml)) return null;

		try
		{
			var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault();
			if (summary is null) return null;

			var text = Whitespace().Replace(summary.Value.Trim(), " ");

			return text.Length == 0 ? null : text;
		}
		catch (System.Xml.XmlException)
		{
			// Malformed documentation is the author's problem and not this call's: an outline that
			// throws over a comment answers nothing about the members the caller asked for.
			return null;
		}
	}

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
