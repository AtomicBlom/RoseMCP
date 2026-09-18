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
	public static async Task<MutationResult<UsingResult>> AddAsync(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		AddUsingRequest request,
		Action<string>? noteSelfWrite,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null)
	{
		var edit = EditPipeline.Begin(
			snapshot, diagnostics, request.ExpectedRevision, request.Apply, request.Verify, noteSelfWrite);

		if (request.Namespaces.Count == 0) throw new ArgumentException("Name at least one namespace to import.");

		progress?.Report($"Reading {Path.GetFileName(request.FilePath)}", 0);

		var document = SymbolLocator.RequireDocument(snapshot.Solution, request.FilePath);

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

		await edit.WriteAsync(solution, cancellationToken);

		if (request.Verify && edit.Changed) progress?.Report("Compiling to see what the import did", 75);

		await edit.VerifyAsync(
			document.FilePath!,
			EditVerification.ProjectsHolding(solution, document.FilePath!),
			cancellationToken);

		var notices = edit.Notices;
		notices.AddRange(Notices(insertion, edit.Verification));
		notices.AddRange(edit.Report());

		var result = new UsingResult
		{
			Revision = snapshot.Revision,
			FilePath = document.FilePath!,
			Added = insertion.Added,
			AlreadyInScope = insertion.AlreadyInScope,
			Applied = edit.Applied,
			Diff = edit.Outcome.Diff,
			Verified = edit.Verification.Ran,
			IntroducedDiagnostics = edit.Introduced,
			ResolvedDiagnosticCount = edit.Verification.ResolvedCount,
			TotalErrorCount = edit.Verification.TotalCount,
			ProjectsChecked = edit.Verification.Projects,
			ChangedFiles = edit.Outcome.ChangedFiles,
			Notices = notices,
		};

		return new MutationResult<UsingResult>(result, edit.Kept);
	}

	/// <summary>
	/// What importing a namespace has to say that no other writing tool does. Everything about the
	/// write and the compile comes from <see cref="EditPipeline.Report"/>, which runs after this.
	/// </summary>
	private static IEnumerable<string> Notices(UsingInsertion insertion, Verification verification)
	{
		if (insertion.Added.Count == 0)
		{
			yield return "Every namespace asked for was in scope already, so the file was not touched.";
		}

		foreach (var covered in insertion.AlreadyInScope)
		{
			yield return $"Did not import {covered}.";
		}

		if (verification.Introduced.Count == 0) yield break;

		// CS0104 is a type name two imported namespaces both have, CS0121 a call two static imports
		// both offer, CS0229 a member name two of them share. Anything else was not made by an
		// ambiguity, and saying it was sends the caller looking for a clash that is not there.
		string[] ambiguities = ["CS0104", "CS0121", "CS0229"];

		var ambiguous = verification.Introduced.Any(entry => ambiguities.Contains(entry.Id));

		yield return ambiguous
			? "The import made a name ambiguous, which is the usual way adding one breaks a file. Qualify "
				+ "the name, or use an alias instead."
			: "Diagnostics appeared after the import that are not an ambiguity; each is listed with where it is.";
	}
}
