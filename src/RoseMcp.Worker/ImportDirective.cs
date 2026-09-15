using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// One import: a namespace, the static members of a type, or an alias.
/// <para>
/// A caller's argument is parsed as the whole directive it would be written as, never as a name.
/// <c>static Foo.Bar</c> handed to a name parser comes back as a tree with errors in it whose text
/// still prints as valid C#, so the file written from it is right while the compilation that
/// verifies it holds a parse error the file does not -- and reports it, confidently, as something
/// the import broke. Nothing writes code it has not parsed, and that includes parsing it as the
/// wrong thing.
/// </para>
/// </summary>
public sealed record ImportDirective
{
	/// <summary>Whether it imports a namespace, a type's static members, or an alias.</summary>
	public required ImportKind Kind { get; init; }

	/// <summary>The namespace or type the directive names, with its whitespace normalised.</summary>
	public required string Target { get; init; }

	/// <summary>The name an alias introduces; null for anything that is not an alias.</summary>
	public string? Alias { get; init; }

	/// <summary>
	/// A <c>global using</c>, which only a directive already in the file can be. The compiler requires
	/// every one to come before the file's other imports, so nothing is placed above one.
	/// </summary>
	public bool Global { get; init; }

	/// <summary>
	/// The import as it is reported and compared: <c>System.Text</c>, <c>static System.Math</c> or
	/// <c>Json = System.Text.Json</c>. The kind is part of it because a namespace import and a static
	/// import of the same name are different imports, and neither covers the other.
	/// </summary>
	public string Text => Kind switch
	{
		ImportKind.Static => $"static {Target}",
		ImportKind.Alias => $"{Alias} = {Target}",
		_ => Target,
	};

	/// <summary>What it sorts by among imports of its own kind: the alias for an alias, the target otherwise.</summary>
	public string SortKey => Alias ?? Target;

	/// <summary>
	/// The block it belongs to where a file separates its imports: namespaces by their first segment,
	/// and all the static imports and all the aliases as one block each. Neither marker can be a
	/// namespace's first segment, since one is a keyword and the other is not an identifier.
	/// </summary>
	public string Group => Kind switch
	{
		ImportKind.Static => "static",
		ImportKind.Alias => "=",
		_ => UsingDirectives.Group(Target),
	};

	/// <summary>A namespace import, for a caller that only ever asks about namespaces.</summary>
	public static ImportDirective Namespace(string name) => new() { Kind = ImportKind.Namespace, Target = name };

	/// <summary>
	/// One import, however the caller wrote it: with or without the <c>using</c> keyword, and with or
	/// without the semicolon.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// It is not exactly one import, or it is one this does not write into a single file.
	/// </exception>
	public static ImportDirective Parse(string requested)
	{
		var text = requested.Trim();

		if (text.EndsWith(';')) text = text[..^1].TrimEnd();

		var hasKeyword = text.StartsWith("using ", StringComparison.Ordinal)
			|| text.StartsWith("global using ", StringComparison.Ordinal);

		var unit = SyntaxFactory.ParseCompilationUnit(hasKeyword ? $"{text};" : $"using {text};");

		var isOneImport = unit.Usings.Count == 1
			&& unit.Externs.Count == 0
			&& unit.AttributeLists.Count == 0
			&& unit.Members.Count == 0
			&& !unit.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

		if (!isOneImport)
		{
			throw new ArgumentException(
				$"'{requested}' is not an import. Name a namespace as System.Text, a type whose static "
					+ "members to import as static System.Math, or an alias as Json = System.Text.Json.");
		}

		var directive = unit.Usings[0];

		if (directive.GlobalKeyword != default)
		{
			throw new ArgumentException(
				$"'{requested}' is a global using, which applies to the whole project rather than to the "
					+ "file being imported into. Write it into the project's global usings file instead.");
		}

		if (directive.UnsafeKeyword != default)
		{
			throw new ArgumentException($"'{requested}' is an unsafe alias, which this does not write.");
		}

		return From(directive);
	}

	/// <summary>The import a directive already in a file makes.</summary>
	public static ImportDirective From(UsingDirectiveSyntax directive) => new()
	{
		Kind = KindOf(directive),
		Target = directive.NamespaceOrType.NormalizeWhitespace().ToString(),
		Alias = directive.Alias?.Name.Identifier.ValueText,
		Global = directive.GlobalKeyword != default,
	};

	/// <summary>
	/// The directive, ready to go into a file. Laid out by NormalizeWhitespace, which is what puts a
	/// space after <c>using</c> and <c>static</c>: SyntaxFactory gives a keyword none, and
	/// <c>usingSystem.Text;</c> parses as a top-level statement with four errors that say nothing
	/// about a missing space.
	/// </summary>
	/// <param name="lineEnding">The line ending the file uses.</param>
	public UsingDirectiveSyntax ToSyntax(string lineEnding) =>
		SyntaxFactory.ParseCompilationUnit($"using {Text};").Usings[0]
			.NormalizeWhitespace()
			.WithTrailingTrivia(SyntaxFactory.EndOfLine(lineEnding));

	private static ImportKind KindOf(UsingDirectiveSyntax directive)
	{
		if (directive.Alias is not null) return ImportKind.Alias;

		return directive.StaticKeyword != default ? ImportKind.Static : ImportKind.Namespace;
	}
}
