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
	/// The members <paramref name="code"/> declares, in the order they were written, re-indented for
	/// a declaration sitting at <paramref name="indent"/>.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// The code does not parse, declares no member, or would put something outside the container.
	/// </exception>
	public static IReadOnlyList<MemberDeclarationSyntax> Parse(
		string code,
		string containerKeyword,
		ParseOptions? options,
		string indent = "")
	{
		if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("No code was supplied, so there is nothing to write.");

		var members = ParseWrapped(code, containerKeyword, options);
		if (indent.Length == 0) return members;

		// Parsed twice, because the indentation cannot be worked out until the code has been
		// understood: which lines sit inside a multi-line literal decides which of them have to be
		// left exactly as they arrived. The second parse is of text, in microseconds, against an edit
		// that is about to compile a project.
		var shifted = Shift(code, indent, LiteralContinuations(members));

		return string.Equals(shifted, code, StringComparison.Ordinal)
			? members
			: ParseWrapped(shifted, containerKeyword, options);
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

	/// <summary>
	/// Parses the code inside a synthetic container and checks the shape of what came out.
	/// </summary>
	private static IReadOnlyList<MemberDeclarationSyntax> ParseWrapped(
		string code,
		string containerKeyword,
		ParseOptions? options)
	{
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
	/// Re-indents code for where it is going: its own baseline indentation off every line, then the
	/// destination's on.
	/// <para>
	/// This is the half of the promise the formatter cannot keep. It reindents statements and moves
	/// braces, which are rules it has, and a line wrapped by hand inside a body comes out right
	/// because of them -- but a wrapped parameter list is layout it has no rule about, so it keeps
	/// whatever indentation arrived. Code written for column zero then lands a level short of its
	/// neighbours, and neither IDE0055 nor dotnet format says a word, because neither of them has an
	/// opinion either. Measured on this repository's own source, writing a member through this tool.
	/// </para>
	/// <para>
	/// Both halves are needed rather than just the shift: a caller that has read the file and
	/// indented for the destination is as likely as one that wrote at column zero, and only removing
	/// the baseline first makes the two the same request.
	/// </para>
	/// </summary>
	private static string Shift(string code, string indent, IReadOnlySet<int> literals)
	{
		var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var baseline = Baseline(lines);

		var shifted = lines.Select((line, index) =>
		{
			if (literals.Contains(index)) return line;

			var stripped = baseline.Length > 0 && line.StartsWith(baseline, StringComparison.Ordinal)
				? line[baseline.Length..]
				: line;

			// The first line's indentation comes from the trivia at the splice point, and padding a
			// blank line only creates trailing whitespace for the next pass to strip again.
			return index > 0 && stripped.Trim().Length > 0 ? indent + stripped : stripped;
		});

		return string.Join("\n", shifted);
	}

	/// <summary>The indentation the code was written at, taken from its first line with content.</summary>
	private static string Baseline(IReadOnlyList<string> lines)
	{
		foreach (var line in lines)
		{
			if (line.Trim().Length == 0) continue;

			return line[..(line.Length - line.TrimStart(' ', '\t').Length)];
		}

		return string.Empty;
	}

	/// <summary>
	/// Lines whose leading whitespace belongs to a string rather than to the layout: every line of a
	/// multi-line literal after its first. Prefixing one of those changes what the program says, and
	/// in a raw literal it changes how much is stripped from all of them.
	/// </summary>
	private static IReadOnlySet<int> LiteralContinuations(IReadOnlyList<MemberDeclarationSyntax> members)
	{
		var lines = new HashSet<int>();

		foreach (var member in members)
		{
			foreach (var node in member.DescendantNodesAndSelf())
			{
				if (node is not (LiteralExpressionSyntax or InterpolatedStringExpressionSyntax)) continue;

				var span = node.SyntaxTree.GetLineSpan(node.Span);

				for (var line = span.StartLinePosition.Line + 1; line <= span.EndLinePosition.Line; line++)
				{
					lines.Add(line - WrapperLines);
				}
			}
		}

		return lines;
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
	private static ArgumentException Rejected(
		string code,
		IReadOnlyList<Diagnostic> errors,
		int lineOffset = WrapperLines,
		int columnOffset = 0)
	{
		var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		var described = errors.Take(Listed).Select(error =>
		{
			var start = error.Location.GetLineSpan().StartLinePosition;
			var line = start.Line - lineOffset;
			var quoted = line >= 0 && line < lines.Length ? lines[line].Trim() : string.Empty;

			return $"line {line + 1}, column {start.Character + 1 - columnOffset}: {error.Id} {error.GetMessage()}"
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
