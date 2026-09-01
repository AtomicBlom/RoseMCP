using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

namespace RoseMcp.XamlStubs;

/// <summary>
/// Turns a project's XAML into stub partials, as a source generator.
/// <para>
/// A generator rather than documents injected by hand, so the output is a source-generated document
/// like any other: readable through rose_read_generated_document, never written to disk, and
/// re-run through the ordinary barrier when a .xaml file changes.
/// </para>
/// <para>
/// It lives in its own assembly, loaded through the worker's shadow-copying loader as an ordinary
/// AnalyzerFileReference, because Roslyn's serializer switches on the concrete type of every
/// analyzer reference when it checksums a project -- which member-level find-references and rename
/// both reach through FindDerivedClasses -- and throws on any type it does not recognise. A custom
/// AnalyzerReference subclass, which is what this used to be, made those two tools fail on every
/// solution containing XAML. Shadow copying means holding this open still does not stop anyone
/// rebuilding it.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class XamlStubGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var markup = context.AdditionalTextsProvider
			.Where(text => text.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
			.Select((text, token) => XamlDocumentReader.Read(text.Path, text.GetText(token)?.ToString() ?? string.Empty))
			.Collect();

		// Combining with the compilation costs the fine-grained caching a generator would normally
		// get: any change re-runs the emit. Unavoidable, because resolving an element to a CLR type
		// is a semantic question. The emit itself is metadata lookups, and measured in milliseconds
		// even on a project with 400 XAML files.
		context.RegisterSourceOutput(markup.Combine(context.CompilationProvider), Emit);
	}

	private static void Emit(
		SourceProductionContext context,
		(ImmutableArray<XamlDocument?> Markup, Compilation Compilation) input)
	{
		var documents = input.Markup.OfType<XamlDocument>().ToArray();
		var unreadable = input.Markup.Count(document => document is null);
		var choice = XamlDialectSelector.Select(input.Compilation, documents);

		if (choice.Dialect is null)
		{
			Report(context, choice, documents.Length + unreadable, 0, [], Unreadable(unreadable));
			return;
		}

		var stubbed = 0;
		var unresolved = new List<string>();
		var skipped = Unreadable(unreadable);
		var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var document in documents)
		{
			context.CancellationToken.ThrowIfCancellationRequested();

			var emission = XamlStubEmitter.Emit(input.Compilation, choice.Dialect, document);

			if (emission.Source is null || emission.HintName is null)
			{
				skipped.Add($"{Path.GetFileName(document.Path)}: {emission.SkipReason}");
				continue;
			}

			// Two markup files claiming the same class is the project's problem, not ours, and a
			// repeated hint name would fail the whole generator run.
			if (!emitted.Add(emission.HintName))
			{
				skipped.Add($"{Path.GetFileName(document.Path)}: {document.ClassName} is declared by another file too");
				continue;
			}

			context.AddSource(emission.HintName, emission.Source);
			unresolved.AddRange(emission.UnresolvedTypes);
			stubbed++;
		}

		Report(context, choice, documents.Length + unreadable, stubbed, unresolved, skipped);
	}

	private static List<string> Unreadable(int count) =>
		count == 0 ? [] : [$"{count} file(s) could not be parsed as XAML"];

	private static void Report(
		SourceProductionContext context,
		XamlDialectChoice choice,
		int markupFiles,
		int stubbed,
		IReadOnlyList<string> unresolved,
		IReadOnlyList<string> skipped) =>
		context.AddSource(
			XamlStubReportChannel.HintName,
			XamlStubReportChannel.Render(new XamlStubReportPayload
			{
				Dialect = choice.Dialect?.Name,
				DialectReason = choice.Reason,
				DialectAmbiguous = choice.WasAmbiguous,
				MarkupFileCount = markupFiles,
				StubbedClassCount = stubbed,
				UnresolvedTypes = [.. unresolved.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
				Skipped = skipped,
			}));
}
