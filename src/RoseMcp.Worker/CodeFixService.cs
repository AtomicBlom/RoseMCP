using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Runs the code fixes a solution's own analyzers ship with, one occurrence or all of them.
/// <para>
/// Diagnostics without fixes leave a caller to edit by hand, which is the reflex this whole project
/// exists to beat -- and hand-fixing the same rule across a hundred files is where it goes wrong
/// most. Roslyn's own <c>FixAllProvider</c> does that correctly; find-and-replace does not.
/// </para>
/// </summary>
public static class CodeFixService
{
	public static async Task<MutationResult<CodeFixResult>> ApplyAsync(
		WorkspaceSnapshot snapshot,
		CodeFixCatalog catalog,
		CodeFixRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		if (request.ExpectedRevision is { } expected && expected != snapshot.Revision)
		{
			throw new InvalidOperationException(
				$"The workspace is at revision {snapshot.Revision}, not the expected {expected}. "
					+ "Something changed underneath this request; re-read and try again.");
		}

		var document = SymbolLocator.FindDocument(snapshot.Solution, request.FilePath)
			?? throw new ArgumentException($"No document in the solution matches '{request.FilePath}'.");

		var scope = ParseScope(request.Scope);
		var notices = new List<string>(snapshot.Notices);

		progress?.Report($"Finding {request.DiagnosticId} occurrences", 5);

		var diagnostics = await FindAsync(catalog, document, request.DiagnosticId, scope, cancellationToken);

		if (diagnostics.IsEmpty)
		{
			return Nothing(snapshot, request, scope, notices, $"No {request.DiagnosticId} in this {request.Scope}.");
		}

		var providers = catalog.ProvidersFor(document.Project, request.DiagnosticId);
		if (providers.IsEmpty)
		{
			return Nothing(
				snapshot,
				request,
				scope,
				notices,
				$"{diagnostics.Length} occurrence(s) of {request.DiagnosticId}, but no analyzer in this project "
					+ "ships a fix for it.");
		}

		progress?.Report("Asking the fixer what it offers", 25);

		var (provider, action) = await ChooseAsync(providers, document, diagnostics, request, cancellationToken);

		if (action is null)
		{
			return Nothing(
				snapshot,
				request,
				scope,
				notices,
				request.FixTitle is { Length: > 0 } wanted
					? $"No fix titled '{wanted}' was offered for {request.DiagnosticId}."
					: $"The fixer for {request.DiagnosticId} offered nothing at that location.");
		}

		progress?.Report($"Applying '{action.Title}' across the {request.Scope}", 40);

		var fixAll = provider.GetFixAllProvider();
		var applied = fixAll is null
			? action
			: await fixAll.GetFixAsync(new FixAllContext(
				document,
				provider,
				scope,
				action.EquivalenceKey,
				[request.DiagnosticId],
				new PrecomputedDiagnostics(diagnostics),
				cancellationToken)) ?? action;

		if (fixAll is null)
		{
			notices.Add($"The fixer for {request.DiagnosticId} cannot fix several at once, so only the first "
				+ $"of {diagnostics.Length} occurrence(s) was fixed.");
		}

		var changed = await ChangedSolutionAsync(applied, snapshot.Solution, cancellationToken) ?? snapshot.Solution;

		progress?.Report(request.Apply ? "Writing the changed files" : "Building the diff", 90);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, changed, request.Apply, noteSelfWrite, cancellationToken);

		// What the diff could not show. A fixer that rewrites a whole file can retype its endings on
		// the way through, and that part of what it did appears in no hunk.
		notices.AddRange(outcome.Notices);

		if (!request.Apply) notices.Add("Preview only; nothing was written to disk.");
		if (outcome.ChangedFiles.Count == 0) notices.Add("The fix produced no changes.");

		var result = new CodeFixResult
		{
			Revision = snapshot.Revision,
			DiagnosticId = request.DiagnosticId,
			FixTitle = action.Title,
			Scope = request.Scope,
			Occurrences = diagnostics.Length,
			ChangedFiles = outcome.ChangedFiles,
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			Diff = outcome.Diff,
			Notices = notices,
		};

		var solution = request.Apply && outcome.ChangedFiles.Count > 0 ? changed : null;

		return new MutationResult<CodeFixResult>(result, solution);
	}

	/// <summary>
	/// What could be fixed in one file. Per document rather than per project, because running every
	/// analyzer over a whole project is the slow path this deliberately avoids.
	/// </summary>
	public static async Task<CodeFixList> ListAsync(
		WorkspaceSnapshot snapshot,
		CodeFixCatalog catalog,
		string filePath,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var document = SymbolLocator.FindDocument(snapshot.Solution, filePath)
			?? throw new ArgumentException($"No document in the solution matches '{filePath}'.");

		progress?.Report("Collecting diagnostics for this file", 10);

		var diagnostics = await InDocumentAsync(document, analyzers: null, catalog, cancellationToken);

		var fixes = new List<AvailableCodeFix>();
		var unfixable = new List<string>();

		foreach (var diagnostic in diagnostics)
		{
			var providers = catalog.ProvidersFor(document.Project, diagnostic.Id);
			var actions = providers.IsEmpty
				? []
				: await OfferedAsync(providers, document, diagnostic, cancellationToken);

			// A provider that claims the id and then offers nothing is, to a caller, the same as no
			// provider at all. Skipping those left the diagnostic out of both lists and so out of the
			// answer entirely -- which is precisely what UnfixableIds exists to prevent, and it was
			// happening to CS0103: two fixers claim it and neither had anything to say.
			if (actions.Count == 0)
			{
				if (!unfixable.Contains(diagnostic.Id, StringComparer.Ordinal)) unfixable.Add(diagnostic.Id);

				continue;
			}

			fixes.Add(new AvailableCodeFix
			{
				DiagnosticId = diagnostic.Id,
				Message = diagnostic.GetMessage(),
				Severity = diagnostic.Severity.ToString(),
				FilePath = document.FilePath ?? filePath,
				Line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
				FixTitles = [.. actions.Select(static action => action.Title).Distinct(StringComparer.Ordinal)],
				SupportsFixAll = providers.Any(static provider => provider.GetFixAllProvider() is not null),
			});
		}

		var notices = new List<string>(snapshot.Notices);

		// Said whenever an unresolved name is present, not only when nothing claimed it. What is
		// registered for CS0103 and CS0246 here is the "generate the missing member" family, which is
		// the wrong fix when the name exists already and only needs importing; the right one,
		// add-import, lives in Microsoft.CodeAnalysis.CSharp.Features -- the IDE layer this server
		// deliberately does not reference.
		if (diagnostics.Any(diagnostic => MissingImports.IsUnresolved(diagnostic.Id)))
		{
			notices.Add("A name that does not resolve has no add-import fix here: that one belongs to the IDE "
				+ "layer, which this server does not load, and what is registered instead offers to generate the "
				+ "missing member. rose_resolve_name finds which namespace it needs and rose_add_using writes it in.");
		}

		return new CodeFixList
		{
			Revision = snapshot.Revision,
			FilePath = document.FilePath ?? filePath,
			Fixes = fixes,
			UnfixableIds = unfixable,
			Notices = notices,
		};
	}

	/// <summary>
	/// Occurrences of one diagnostic id across the scope being fixed.
	/// <para>
	/// Only the analyzers that report that id are run, which is what makes fixing one rule across a
	/// project affordable: a full analyzer pass is seconds to minutes, and this is a fraction of it
	/// for exactly the same answer.
	/// </para>
	/// </summary>
	private static async Task<ImmutableArray<Diagnostic>> FindAsync(
		CodeFixCatalog catalog,
		Document document,
		string diagnosticId,
		FixAllScope scope,
		CancellationToken cancellationToken)
	{
		var projects = scope == FixAllScope.Solution
			? document.Project.Solution.Projects
			: [document.Project];

		var found = ImmutableArray.CreateBuilder<Diagnostic>();

		foreach (var project in projects)
		{
			var analyzers = catalog.AnalyzersFor(project, diagnosticId);
			var compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null) continue;

			var fromCompiler = compilation.GetDiagnostics(cancellationToken)
				.Where(diagnostic => string.Equals(diagnostic.Id, diagnosticId, StringComparison.Ordinal));

			found.AddRange(fromCompiler);

			if (!analyzers.IsEmpty)
			{
				var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
				var reported = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);

				found.AddRange(reported.Where(diagnostic =>
					string.Equals(diagnostic.Id, diagnosticId, StringComparison.Ordinal)));
			}
		}

		var all = found.ToImmutable();

		if (scope != FixAllScope.Document) return all;

		return [.. all.Where(diagnostic => diagnostic.Location.SourceTree is { } tree
			&& string.Equals(tree.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase))];
	}

	/// <summary>Every diagnostic in one document, compiler and analyzer alike.</summary>
	private static async Task<IReadOnlyList<Diagnostic>> InDocumentAsync(
		Document document,
		ImmutableArray<DiagnosticAnalyzer>? analyzers,
		CodeFixCatalog catalog,
		CancellationToken cancellationToken)
	{
		var model = await document.GetSemanticModelAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		if (model is null || tree is null) return [];

		var diagnostics = new List<Diagnostic>(model.GetDiagnostics(cancellationToken: cancellationToken));

		var all = analyzers ?? AllAnalyzers(document.Project);
		if (all.IsEmpty) return diagnostics;

		var compilation = await document.Project.GetCompilationAsync(cancellationToken);
		if (compilation is null) return diagnostics;

		// Per tree and per model rather than per compilation, which is the difference between a
		// question about one file and a pass over the whole project.
		var withAnalyzers = compilation.WithAnalyzers(all, document.Project.AnalyzerOptions);

		diagnostics.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken));
		diagnostics.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, null, cancellationToken));

		return diagnostics;
	}

	private static ImmutableArray<DiagnosticAnalyzer> AllAnalyzers(Project project) =>
		[.. project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(project.Language))];

	/// <summary>The fix to run: the one whose title was asked for, or the first offered.</summary>
	private static async Task<(CodeFixProvider Provider, CodeAction? Action)> ChooseAsync(
		ImmutableArray<CodeFixProvider> providers,
		Document document,
		ImmutableArray<Diagnostic> diagnostics,
		CodeFixRequest request,
		CancellationToken cancellationToken)
	{
		var first = diagnostics
			.Where(diagnostic => diagnostic.Location.SourceTree is not null)
			.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
			.FirstOrDefault() ?? diagnostics[0];

		var owner = document.Project.Solution.GetDocument(first.Location.SourceTree) ?? document;

		foreach (var provider in providers)
		{
			var actions = await OfferedAsync([provider], owner, first, cancellationToken);

			var match = request.FixTitle is { Length: > 0 } wanted
				? actions.FirstOrDefault(action => action.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))
				: actions.FirstOrDefault();

			if (match is not null) return (provider, match);
		}

		return (providers[0], null);
	}

	private static async Task<IReadOnlyList<CodeAction>> OfferedAsync(
		IEnumerable<CodeFixProvider> providers,
		Document document,
		Diagnostic diagnostic,
		CancellationToken cancellationToken)
	{
		var actions = new List<CodeAction>();

		foreach (var provider in providers)
		{
			var context = new CodeFixContext(
				document,
				diagnostic,
				(action, _) => actions.Add(action),
				cancellationToken);

			try
			{
				await provider.RegisterCodeFixesAsync(context);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// A fixer that throws on someone else's code is a fixer we do not use, not a failed
				// request. There is nothing the caller could do with its stack trace.
			}
		}

		return actions;
	}

	private static async Task<Solution?> ChangedSolutionAsync(
		CodeAction action,
		Solution original,
		CancellationToken cancellationToken)
	{
		var operations = await action.GetOperationsAsync(cancellationToken);

		return operations
			.OfType<ApplyChangesOperation>()
			.Select(static operation => operation.ChangedSolution)
			.LastOrDefault();
	}

	private static FixAllScope ParseScope(string scope) => scope.ToLowerInvariant() switch
	{
		"document" or "file" => FixAllScope.Document,
		"project" => FixAllScope.Project,
		"solution" => FixAllScope.Solution,
		_ => throw new ArgumentException($"Unknown scope '{scope}'. Use document, project, or solution."),
	};

	private static MutationResult<CodeFixResult> Nothing(
		WorkspaceSnapshot snapshot,
		CodeFixRequest request,
		FixAllScope scope,
		List<string> notices,
		string reason)
	{
		notices.Add(reason);

		return new MutationResult<CodeFixResult>(
			new CodeFixResult
			{
				Revision = snapshot.Revision,
				DiagnosticId = request.DiagnosticId,
				FixTitle = string.Empty,
				Scope = scope.ToString(),
				Occurrences = 0,
				ChangedFiles = [],
				Applied = false,
				Diff = string.Empty,
				Notices = notices,
			},
			Solution: null);
	}
}
