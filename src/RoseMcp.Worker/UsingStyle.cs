using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoseMcp.Worker;

/// <summary>
/// How one file arranges its imports: what order, and whether groups are separated.
/// <para>
/// Read from the file first and .editorconfig second, which is the opposite of everywhere else here
/// and deliberate. This repository sets <c>dotnet_separate_import_directive_groups = false</c> and
/// every file in it separates its groups anyway -- the setting only stops the analyzer insisting,
/// it does not forbid it. Matching what the file does is what keeps an added import out of the
/// diff, and it is the same reasoning that has the line ending fall back to what the file already
/// uses.
/// </para>
/// </summary>
public sealed record UsingStyle
{
	/// <summary>System imports sort before the rest, which is the language tooling's own default.</summary>
	public required bool SystemFirst { get; init; }

	/// <summary>A blank line between groups of imports that share a first segment.</summary>
	public required bool SeparateGroups { get; init; }

	public required string LineEnding { get; init; }

	public static UsingStyle For(Project project, SyntaxTree tree, CompilationUnitSyntax root, string lineEnding)
	{
		var options = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);

		return new UsingStyle
		{
			SystemFirst = Flag(options, "dotnet_sort_system_directives_first") ?? true,
			SeparateGroups = Separated(root) ?? Flag(options, "dotnet_separate_import_directive_groups") ?? false,
			LineEnding = lineEnding,
		};
	}

	/// <summary>
	/// Whether this file puts a blank line between groups, or null when it has no two groups to tell
	/// from. Answered by looking at the first place the group changes: one example is enough, because
	/// a file is consistent about this or it is not a file anybody is maintaining.
	/// </summary>
	private static bool? Separated(CompilationUnitSyntax root)
	{
		for (var index = 1; index < root.Usings.Count; index++)
		{
			var previous = First(root.Usings[index - 1]);
			var current = First(root.Usings[index]);

			if (previous == current) continue;

			return root.Usings[index].GetLeadingTrivia().Any(SyntaxKind.EndOfLineTrivia);
		}

		return null;
	}

	private static string First(UsingDirectiveSyntax directive)
	{
		var name = directive.Name?.ToString() ?? string.Empty;
		var dot = name.IndexOf('.', StringComparison.Ordinal);

		return dot < 0 ? name : name[..dot];
	}

	private static bool? Flag(AnalyzerConfigOptions options, string key)
	{
		if (!options.TryGetValue(key, out var value)) return null;

		return bool.TryParse(value.Trim(), out var parsed) ? parsed : null;
	}
}
