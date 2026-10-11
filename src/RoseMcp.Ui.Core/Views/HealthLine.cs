using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// How loudly a workspace's health is drawn. Colour is for what somebody can act on, and for nothing else: a reason
/// accepted, a note, a stopped row whose reason is that nobody used it, are all quiet however much they say.
/// </summary>
public enum HealthTone
{
	/// <summary>Nothing to do: ready, or a state that says what it is without needing anybody.</summary>
	Quiet,

	/// <summary>On its way: loading, or closing.</summary>
	Working,

	/// <summary>Some answers may be wrong and somebody can do something: reload for a rebuilt analyzer, accept or fix a reason.</summary>
	Caution,

	/// <summary>No answer can be trusted until somebody acts: a reason that blocks answers, a worker that faulted.</summary>
	Critical,
}

/// <summary>
/// A workspace's health in one line, and its tone: the one line the Workspaces view gives a row, with the detail a
/// click away. Derived from the shaped reasons the broker gives -- their effect, and whether the person accepted
/// them -- and from the worker's state, never from the wording of a reason.
/// </summary>
/// <param name="Text">The line.</param>
/// <param name="Tone">How loudly to draw it.</param>
public sealed record HealthLine(string Text, HealthTone Tone)
{
	/// <summary>The line for <paramref name="workspace"/>.</summary>
	public static HealthLine Of(OperatorWorkspace workspace)
	{
		var summary = workspace.Summary;

		if (!summary.Alive)
		{
			var faulted = summary.State == WorkspaceState.Faulted;
			return new HealthLine(FirstSentence(summary.ExitReason, "Stopped"), faulted ? HealthTone.Critical : HealthTone.Quiet);
		}

		if (summary.State == WorkspaceState.Loading) return new HealthLine("Loading", HealthTone.Working);
		if (summary.State == WorkspaceState.PendingUnload) return new HealthLine("Closing", HealthTone.Working);

		var unaccepted = summary.Health.Where(reason => reason.Accepted is null && reason.Effect != HealthEffect.Note).ToList();
		var blocking = unaccepted.FirstOrDefault(reason => reason.Effect == HealthEffect.BlocksAnswers);

		if (blocking is not null) return new HealthLine(WithMore(blocking.Line, unaccepted.Count - 1), HealthTone.Critical);

		if (summary.State == WorkspaceState.Faulted)
		{
			return new HealthLine(FirstSentence(summary.DegradedReasons.FirstOrDefault(), "Faulted"), HealthTone.Critical);
		}

		// Before the reasons that may affect some projects: a rebuilt analyzer touches every project that loads it,
		// and Reload is the whole fix.
		if (workspace.RebuiltAnalyzers.Count > 0)
		{
			var rebuilt = workspace.RebuiltAnalyzers.Count == 1
				? $"{Format.FileLine(workspace.RebuiltAnalyzers[0], line: null)} was rebuilt"
				: $"{workspace.RebuiltAnalyzers.Count} analyzers were rebuilt";

			return new HealthLine($"{rebuilt}. Answers use the old build until a reload.", HealthTone.Caution);
		}

		if (unaccepted.Count > 0) return new HealthLine(WithMore(unaccepted[0].Line, unaccepted.Count - 1), HealthTone.Caution);

		// A degraded worker whose reasons carry no shape still says why, in the worker's own words.
		var unshaped = summary.State == WorkspaceState.Degraded && summary.Health.Count == 0 && summary.DegradedReasons.Count > 0;
		if (unshaped) return new HealthLine(FirstSentence(summary.DegradedReasons[0], "Degraded"), HealthTone.Caution);

		var accepted = summary.Health.Count(reason => reason.Accepted is not null);

		return accepted > 0
			? new HealthLine($"Ready{Format.Separator}{Format.Count(accepted, "accepted warning")}", HealthTone.Quiet)
			: new HealthLine("Ready", HealthTone.Quiet);
	}

	/// <summary>A reason's first sentence, which is the part a line has room for.</summary>
	public static string FirstSentence(string? text, string fallback)
	{
		if (string.IsNullOrWhiteSpace(text)) return fallback;

		var line = text.Split('\n')[0].Trim();
		var stop = line.IndexOf(". ", StringComparison.Ordinal);

		return stop < 0 ? line : line[..(stop + 1)];
	}

	private static string WithMore(string line, int more) =>
		more > 0 ? $"{line} (and {Format.Count(more, "more reason")})" : line;
}
