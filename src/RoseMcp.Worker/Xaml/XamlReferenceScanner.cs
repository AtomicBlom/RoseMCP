using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Finds the places markup names a symbol, so a rename can say what it is about to break.
/// <para>
/// This is the one class of breakage a C#-only tool cannot see and the compiler will not catch
/// either. Renaming a view model's <c>Title</c> property moves every C# reference correctly and
/// leaves <c>{Binding Title}</c> in forty markup files pointing at nothing -- which builds, runs, and
/// silently shows an empty control. Bindings resolve at runtime against a DataContext that only the
/// running application knows, so nothing here can prove a mention refers to the symbol being
/// renamed. It is reported for a person to judge, and never rewritten.
/// </para>
/// </summary>
public static class XamlReferenceScanner
{
	private const RegexOptions Options = RegexOptions.CultureInvariant;

	/// <summary>A double quote, spelled this way so the patterns below stay readable.</summary>
	private const string Quote = "\"";

	/// <summary>
	/// Markup that mentions <paramref name="name"/>, across every XAML file the workspace knows about.
	/// <para>
	/// Read from the additional documents rather than from disk, because those are already tracked and
	/// reconciled -- the same barrier that keeps C# results fresh applies to the markup.
	/// </para>
	/// </summary>
	public static async Task<IReadOnlyList<XamlMention>> FindAsync(
		Solution solution,
		string name,
		CancellationToken cancellationToken)
	{
		if (name.Length == 0) return [];

		var mentions = new List<XamlMention>();

		foreach (var project in solution.Projects)
		{
			foreach (var document in project.AdditionalDocuments)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (document.FilePath is not { Length: > 0 } path) continue;
				if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

				var text = await document.GetTextAsync(cancellationToken);

				mentions.AddRange(InText(path, text.ToString(), name));
			}
		}

		return [.. mentions
			.DistinctBy(mention => (mention.FilePath, mention.Line, mention.Kind))
			.OrderBy(mention => mention.FilePath, StringComparer.OrdinalIgnoreCase)
			.ThenBy(mention => mention.Line)];
	}

	/// <summary>
	/// Mentions in one file's markup, anchored to XAML syntax rather than to free text: a name inside
	/// prose in a comment is not a reference, and a name in a binding path is.
	/// </summary>
	public static IEnumerable<XamlMention> InText(string path, string text, string name)
	{
		var escaped = Regex.Escape(name);

		var patterns = new (string Kind, Regex Pattern)[]
		{
			("x:Name", new Regex("(x:Name|Name)\\s*=\\s*" + Quote + escaped + Quote, Options)),
			("element", new Regex("</?(\\w+:)?" + escaped + "[\\s/>]", Options)),
			("binding", new Regex(
				"\\{[^}]*\\b(Binding|x:Bind|TemplateBinding)\\b[^}]*\\b" + escaped + "\\b[^}]*\\}",
				Options)),
			// A dotted value counts too: x:Class="Ui.Widget" breaks in exactly the same way a binding
			// path does, and renaming a page's class is when it happens.
			("attribute value", new Regex(
				"=\\s*" + Quote + "(\\w+\\.)*" + escaped + Quote,
				Options)),
		};

		var lines = text.Split('\n');

		for (var index = 0; index < lines.Length; index++)
		{
			var line = lines[index].TrimEnd('\r');

			foreach (var (kind, pattern) in patterns)
			{
				if (!pattern.IsMatch(line)) continue;

				yield return new XamlMention
				{
					FilePath = path,
					Line = index + 1,
					Kind = kind,
					Text = line.Trim(),
				};

				// One mention per line is enough to send somebody to look, and a binding that also
				// matches "attribute value" is one finding rather than two.
				break;
			}
		}
	}
}
