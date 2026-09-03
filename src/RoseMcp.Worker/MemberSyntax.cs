using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoseMcp.Worker;

/// <summary>
/// Turns supplied C# into member declarations, or refuses before anything is written.
/// <para>
/// The refusing is most of the value. Every mechanical failure a text edit produces -- an
/// unbalanced brace, a dropped access modifier, an escape that leaked into the source, a
/// <c>&lt;/summary&gt;</c> with no opener -- is something the parser sees and a diff does not, and
/// failing here costs nothing. Failing at the next build costs a build, and leaves the file broken
/// until someone pays for it.
/// </para>
/// <para>
/// The code is parsed inside a synthetic container of the same kind as the real one rather than on
/// its own, because a member declaration only means anything in a container. An enum member is not
/// a declaration anywhere else, and parsing a bare snippet as a compilation unit turns
/// <c>void M() { }</c> into a top-level local function and <c>int x = 1;</c> into a statement --
/// both of which parse cleanly and mean something entirely different from what was asked for.
/// </para>
/// </summary>
public static class MemberSyntax
{
	/// <summary>How many errors are worth listing. Past the first few they are usually consequences.</summary>
	private const int Listed = 5;

	/// <summary>Never compiled and never written; only the parser ever sees it.</summary>
	private const string WrapperName = "__RoseMcpContainer";

	/// <summary>The lines the wrapper adds above the code, so errors are reported in the caller's terms.</summary>
	private const int WrapperLines = 2;

	/// <summary>
	/// The members <paramref name="code"/> declares, in the order they were written.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// The code does not parse, declares no member, or would put something outside the container.
	/// </exception>
	public static IReadOnlyList<MemberDeclarationSyntax> Parse(
		string code,
		string containerKeyword,
		ParseOptions? options)
	{
		if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("No code was supplied, so there is nothing to write.");

		var wrapped = $"{containerKeyword} {WrapperName}\n{{\n{code.TrimEnd()}\n}}\n";
		var tree = CSharpSyntaxTree.ParseText(wrapped, options as CSharpParseOptions);

		var errors = tree.GetDiagnostics()
			.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
			.ToArray();

		if (errors.Length > 0) throw Rejected(code, errors);

		// One container and nothing beside it. A brace closed once too often parses without any
		// error at all by ending the wrapper early, which would otherwise carry whatever follows
		// past every check here and land it in the file at top level.
		if (((CompilationUnitSyntax)tree.GetRoot()).Members is not [BaseTypeDeclarationSyntax wrapper]
			|| wrapper.Identifier.Text != WrapperName)
		{
			throw new ArgumentException(
				"The code closes more braces than it opens, so part of it would end up outside the member. "
					+ "Supply the member declarations alone.");
		}

		GuardDanglingComment(wrapper);

		var members = Members(wrapper);
		if (members.Count == 0) throw new ArgumentException("The code declares no member, so there is nothing to write.");

		return members;
	}

	/// <summary>
	/// The keyword a container was declared with, so the synthetic one parses by the same rules as
	/// the real one.
	/// </summary>
	public static string KeywordOf(BaseTypeDeclarationSyntax declaration) => declaration switch
	{
		RecordDeclarationSyntax record when !record.ClassOrStructKeyword.IsKind(SyntaxKind.None) =>
			$"{record.Keyword.Text} {record.ClassOrStructKeyword.Text}",
		TypeDeclarationSyntax type => type.Keyword.Text,
		EnumDeclarationSyntax => "enum",
		_ => "class",
	};

	/// <summary>True for a comment of any kind, documentation included.</summary>
	public static bool IsComment(SyntaxTrivia trivia) => trivia.Kind() is
		SyntaxKind.SingleLineCommentTrivia
			or SyntaxKind.MultiLineCommentTrivia
			or SyntaxKind.SingleLineDocumentationCommentTrivia
			or SyntaxKind.MultiLineDocumentationCommentTrivia;

	/// <summary>
	/// Refuses a comment written after the last member. It attaches to the container's closing brace
	/// rather than to any member, so it is not part of anything being written and would be dropped
	/// without trace -- and a lost comment is invisible in the diff that reports the change.
	/// </summary>
	private static void GuardDanglingComment(BaseTypeDeclarationSyntax wrapper)
	{
		if (!wrapper.CloseBraceToken.LeadingTrivia.Any(IsComment)) return;

		throw new ArgumentException(
			"The code ends with a comment that belongs to no member, so it would be dropped. Put it above "
				+ "the member it describes.");
	}

	private static IReadOnlyList<MemberDeclarationSyntax> Members(BaseTypeDeclarationSyntax wrapper) => wrapper switch
	{
		TypeDeclarationSyntax type => type.Members,
		EnumDeclarationSyntax @enum => [.. @enum.Members],
		_ => [],
	};

	/// <summary>
	/// The parse errors, each located in the code the caller sent rather than in the wrapper they
	/// never saw, and quoting the line so the message can be acted on without reading anything back.
	/// </summary>
	private static ArgumentException Rejected(string code, IReadOnlyList<Diagnostic> errors)
	{
		var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		var described = errors.Take(Listed).Select(error =>
		{
			var start = error.Location.GetLineSpan().StartLinePosition;
			var line = start.Line - WrapperLines;
			var quoted = line >= 0 && line < lines.Length ? lines[line].Trim() : string.Empty;

			return $"line {line + 1}, column {start.Character + 1}: {error.Id} {error.GetMessage()}"
				+ (quoted.Length > 0 ? $"  ->  {quoted}" : string.Empty);
		});

		var more = errors.Count > Listed ? $" ... and {errors.Count - Listed} more." : string.Empty;

		// A using directive is the one rejection whose cause is not the code but the tool's scope,
		// so it is worth saying rather than leaving as a bare "type expected".
		var hint = code.TrimStart().StartsWith("using ", StringComparison.Ordinal)
			? " This writes a member, not a file: a using directive belongs above the namespace and has to be added separately."
			: string.Empty;

		return new ArgumentException(
			$"The code does not parse, so nothing was written. {string.Join("; ", described)}{more}{hint}");
	}
}
