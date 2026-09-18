using Microsoft.CodeAnalysis;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// The half of a writing tool that is the same in all of them: refuse a stale revision, write what
/// was rewritten, compile to see what the write did, and report the same things about all of it.
/// <para>
/// Each tool differs in how it finds what to change and how it rewrites it, and in nothing after
/// that. Copied rather than shared, the part after that has already drifted: of the ten tools that
/// write, two say a file already said exactly what was asked, four say nothing was compiled when
/// nothing was, and two say how many errors were there beforehand. Which lines a tool carries
/// records which sibling it was copied from, not anything about the tool.
/// </para>
/// <para>
/// So the sequence is a type, and a tool supplies the rewrite. What a tool has to say for itself
/// goes after <see cref="Report"/>, which is the shared part and the part a tool added later gets
/// without having to know it exists.
/// </para>
/// </summary>
internal sealed class EditPipeline
{
	/// <summary>
	/// How many introduced errors a result lists before it stops and says how many there were. A
	/// result is read by an agent with a token budget, and three hundred entries of a cascade say no
	/// more than the first twenty and the count.
	/// </summary>
	internal const int Listed = 20;

	private readonly WorkspaceSnapshot _snapshot;
	private readonly DiagnosticsService _diagnostics;
	private readonly Action<string>? _noteSelfWrite;
	private readonly bool _apply;
	private readonly bool _verify;

	private EditPipeline(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		bool apply,
		bool verify,
		Action<string>? noteSelfWrite)
	{
		_snapshot = snapshot;
		_diagnostics = diagnostics;
		_apply = apply;
		_verify = verify;
		_noteSelfWrite = noteSelfWrite;
		Solution = snapshot.Solution;
		Notices = [.. snapshot.Notices];
	}

	/// <summary>
	/// Starts an edit, refusing one written against a revision the workspace has moved past. Nothing
	/// has been read or located yet, so a refusal here costs the caller nothing.
	/// </summary>
	/// <exception cref="InvalidOperationException">The workspace has moved past <paramref name="expectedRevision"/>.</exception>
	internal static EditPipeline Begin(
		WorkspaceSnapshot snapshot,
		DiagnosticsService diagnostics,
		long? expectedRevision,
		bool apply,
		bool verify,
		Action<string>? noteSelfWrite)
	{
		snapshot.RefuseIfMoved(expectedRevision);

		return new EditPipeline(snapshot, diagnostics, apply, verify, noteSelfWrite);
	}

	/// <summary>What this edit has to tell its caller, in the order it will be read.</summary>
	internal List<string> Notices { get; }

	/// <summary>The solution as it now stands, which is the snapshot's until something is written.</summary>
	internal Solution Solution { get; private set; }

	/// <summary>What the write did.</summary>
	internal WriteOutcome Outcome { get; private set; } = new() { ChangedFiles = [], Diff = string.Empty };

	/// <summary>What compiling after the write found, or that nothing was compiled.</summary>
	internal Verification Verification { get; private set; } = Verification.NotRun;

	/// <summary>Whether the write changed anything, which is not the same as whether it was asked to.</summary>
	internal bool Changed => Outcome.ChangedFiles.Count > 0;

	/// <summary>Whether anything reached disk: asked for, and something to write.</summary>
	internal bool Applied => _apply && Changed;

	/// <summary>The solution to keep, or null when nothing was written and there is nothing to keep.</summary>
	internal Solution? Kept => Applied ? Solution : null;

	/// <summary>The introduced errors a result carries, cut to <see cref="Listed"/>.</summary>
	internal IReadOnlyList<DiagnosticEntry> Introduced => [.. Verification.Introduced.Take(Listed)];

	/// <summary>
	/// Writes <paramref name="written"/>, or works out what writing it would do when the caller asked
	/// for a preview.
	/// </summary>
	internal async Task WriteAsync(Solution written, CancellationToken cancellationToken)
	{
		Solution = written;
		Outcome = await SolutionWriter.ApplyAsync(_snapshot.Solution, written, _apply, _noteSelfWrite, cancellationToken);

		if (!Changed) Notices.Add("The file already said exactly that, so nothing changed.");
	}

	/// <summary>
	/// Compiles <paramref name="scope"/> to see what the write did, when the caller asked and there
	/// is something to have done it.
	/// <para>
	/// A preview is verified too: what an edit would break is the question a preview is asking.
	/// </para>
	/// </summary>
	internal async Task VerifyAsync(string path, IReadOnlyList<string> scope, CancellationToken cancellationToken)
	{
		if (!_verify || !Changed) return;

		Verification = await EditVerification.RunAsync(
			_diagnostics, _snapshot.Solution, Solution, scope, path, cancellationToken);
	}

	/// <summary>
	/// Writes and compiles again, for a rewrite made in the light of what the first compile found.
	/// The scope is the one already compiled, so the two answers are about the same projects.
	/// </summary>
	internal async Task RewriteAsync(
		Solution rewritten,
		string path,
		IReadOnlyList<string> scope,
		CancellationToken cancellationToken)
	{
		if (ReferenceEquals(rewritten, Solution)) return;

		Solution = rewritten;

		Outcome = await SolutionWriter.ApplyAsync(
			_snapshot.Solution, rewritten, _apply, _noteSelfWrite, cancellationToken);

		Verification = await EditVerification.RunAsync(
			_diagnostics, _snapshot.Solution, rewritten, scope, path, cancellationToken);
	}

	/// <summary>
	/// What every writing tool says about what it did, in the order a reader needs it: what was
	/// written, then what compiling it found, then what to do about that.
	/// <para>
	/// A tool's own lines come after these. Nothing here knows what the tool was for, which is what
	/// makes it the same sentence from all of them.
	/// </para>
	/// </summary>
	internal IEnumerable<string> Report()
	{
		if (!_apply) yield return "Preview only; nothing was written to disk.";

		// What the diff could not show. Said before the verification lines, because a caller reading a
		// result whose diff looks empty is asking about the write rather than about the compile.
		foreach (var notice in Outcome.Notices) yield return notice;

		if (!Verification.Ran)
		{
			if (Changed)
			{
				yield return "Nothing was compiled, so this says nothing about whether the code is sound. Pass "
					+ "verify=true, or ask rose_diagnostics.";
			}

			yield break;
		}

		foreach (var notice in Verification.Notices) yield return notice;

		var compiled = string.Join(", ", Verification.Projects);

		if (Verification.Introduced.Count > Listed)
		{
			yield return $"Showing {Listed} of the {Verification.Introduced.Count} errors this introduced.";
		}

		if (Verification.TotalCount == 0) yield return $"{compiled} compiles clean.";

		var existing = Verification.TotalCount - Verification.Introduced.Count;

		if (existing > 0)
		{
			// The count is analyzer-inclusive wherever the edit wrote, and rose_diagnostics leaves
			// analyzers out by default, so the bare advice sends a caller to a tool that answers 0
			// about 297 errors, which reads as the two disagreeing rather than as a default.
			yield return Verification.AnalyzedProjects.Count == 0
				? $"{existing} error(s) in {compiled} were there before this edit; ask rose_diagnostics for those."
				: $"{existing} error(s) in {compiled} were there before this edit; ask rose_diagnostics with "
					+ "includeAnalyzers=true for those, since this count includes the analyzer diagnostics it "
					+ "leaves out by default.";
		}

		// The namespace itself, where the compilation could work it out. This is the answer the caller
		// needs next, and without it the next step is going back to editing text by hand.
		foreach (var suggestion in Verification.Suggestions) yield return suggestion;
	}
}
