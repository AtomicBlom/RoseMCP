using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Ensures a file imports what it needs, for code that arrived some other way.
/// <para>
/// The write tools take the same imports in the call that writes the code, which is where the need
/// is usually discovered. This is for the other half: a file written with Write, a member edited by
/// hand, a diagnostic that turned out to be one missing import away from resolving.
/// </para>
/// </summary>
public static class AddUsingService
{
	private const int Listed = 20;

	public static async Task<MutationResult<UsingResult>> AddAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		AddUsingRequest request,
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

		if (request.Namespaces.Count == 0) throw new ArgumentException("Name at least one namespace to import.");

		progress?.Report($"Reading {Path.GetFileName(request.FilePath)}", 0);

		var document = SymbolLocator.FindDocument(snapshot.Solution, request.FilePath)
			?? throw new ArgumentException($"No document in the solution matches '{request.FilePath}'.");

		var root = await document.GetSyntaxRootAsync(cancellationToken);
		var model = await document.GetSemanticModelAsync(cancellationToken);
		var tree = await document.GetSyntaxTreeAsync(cancellationToken);
		var text = await document.GetTextAsync(cancellationToken);

		if (root is not CompilationUnitSyntax unit || model is null || tree is null)
		{
			throw new InvalidOperationException($"{Path.GetFileName(request.FilePath)} is not a C# source file.");
		}

		var rules = Whitespace.RulesFor(document.Project, tree, text);
		var style = UsingStyle.For(document.Project, tree, unit, rules.LineEnding);

		progress?.Report("Working out which are already in scope", 20);

		var insertion = UsingDirectives.Ensure(unit, model, request.Namespaces, style, cancellationToken);

		var solution = insertion.Changed
			? snapshot.Solution.WithDocumentSyntaxRoot(document.Id, insertion.Root)
			: snapshot.Solution;

		progress?.Report(request.Apply ? "Writing the file" : "Building the diff", 55);

		var outcome = await SolutionWriter.ApplyAsync(
			snapshot.Solution, solution, request.Apply, noteSelfWrite, cancellationToken);

		var verification = Verification.NotRun;

		if (request.Verify && outcome.ChangedFiles.Count > 0)
		{
			progress?.Report("Compiling to see what the import did", 75);

			verification = await EditVerification.RunAsync(
				diagnostics,
				snapshot.Solution,
				solution,
				EditVerification.ProjectsHolding(solution, document.FilePath!),
				document.FilePath,
				cancellationToken);
		}

		var notices = new List<string>(snapshot.Notices);
		notices.AddRange(Notices(request, insertion, verification, outcome));

		var result = new UsingResult
		{
			Revision = snapshot.Revision,
			FilePath = document.FilePath!,
			Added = insertion.Added,
			AlreadyInScope = insertion.AlreadyInScope,
			Applied = request.Apply && outcome.ChangedFiles.Count > 0,
			Diff = outcome.Diff,
			Verified = verification.Ran,
			IntroducedDiagnostics = [.. verification.Introduced.Take(Listed)],
			ResolvedDiagnosticCount = verification.ResolvedCount,
			TotalErrorCount = verification.TotalCount,
			ProjectsChecked = verification.Projects,
			ChangedFiles = outcome.ChangedFiles,
			Notices = notices,
		};

		var changed = request.Apply && outcome.ChangedFiles.Count > 0 ? solution : null;

		return new MutationResult<UsingResult>(result, changed);
	}

	private static IEnumerable<string> Notices(
		AddUsingRequest request,
		UsingInsertion insertion,
		Verification verification,
		WriteOutcome outcome)
	{
		if (!request.Apply) yield return "Preview only; nothing was written to disk.";

		// What the diff could not show. An import goes in as one line, so a diff that carries more
		// than that has had the file's endings rewritten around it.
		foreach (var notice in outcome.Notices) yield return notice;

		if (insertion.Added.Count == 0)
		{
			yield return "Every namespace asked for was in scope already, so the file was not touched.";
		}

		foreach (var covered in insertion.AlreadyInScope)
		{
			yield return $"Did not import {covered}.";
		}

		if (!verification.Ran)
		{
			if (outcome.ChangedFiles.Count > 0)
			{
				yield return "Nothing was compiled, so this says nothing about what the import resolved.";
			}

			yield break;
		}

		if (verification.ResolvedCount > 0)
		{
			yield return $"{verification.ResolvedCount} error(s) went away.";
		}

		if (verification.Introduced.Count > 0)
		{
			yield return "The import made something ambiguous, which is the one way adding one can break a "
				+ "file. Qualify the name, or use an alias instead.";
		}
	}
}
