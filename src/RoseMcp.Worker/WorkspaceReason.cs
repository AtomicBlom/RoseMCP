using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// One reason in a workspace's health as the worker holds it: the shape it is reported with, and its text
/// in full, of which the reported shape carries only the opening sentence.
/// <para>
/// The text is what <see cref="WorkspaceStatusReport.DegradedReasons"/> and
/// <see cref="WorkspaceStatusReport.Notices"/> say, written by the same functions as ever; the kind, the
/// effect and the projects are what a person accepts and an agent acknowledges, and they are decided where
/// each text is, so the two cannot disagree about which reason is which.
/// </para>
/// </summary>
/// <param name="Kind">Which cause it is, one of <see cref="ReasonKinds"/>.</param>
/// <param name="Effect">What it does to answers.</param>
/// <param name="Projects">The projects it covers, by name; empty for the whole solution.</param>
/// <param name="Text">The reason in full, its fix included.</param>
public sealed record WorkspaceReason(string Kind, HealthEffect Effect, IReadOnlyList<string> Projects, string Text)
{
	/// <summary>
	/// What a reason naming no project is about, which its acceptance is keyed on in place of a project: the
	/// assemblies that would not load, the platform chosen. Empty where it names projects.
	/// </summary>
	public IReadOnlyList<string> Subjects { get; init; } = [];

	/// <summary>
	/// A reason covering <paramref name="projects"/>, each named once, in the order first given; or, naming
	/// none, about <paramref name="subjects"/>. One naming neither is about its own opening sentence, so an
	/// acceptance of it covers that cause as worded and not whatever else of its kind is said later.
	/// </summary>
	public static WorkspaceReason Of(
		string kind,
		HealthEffect effect,
		string text,
		IEnumerable<string>? projects = null,
		IEnumerable<string>? subjects = null)
	{
		var named = Distinct(projects);
		var about = named.Length > 0 ? [] : Distinct(subjects);

		return new WorkspaceReason(kind, effect, named, text)
		{
			Subjects = named.Length == 0 && about.Length == 0 ? [WorkspaceStatusReporter.LineOf(text)] : about,
		};

		static string[] Distinct(IEnumerable<string>? items) =>
			[.. (items ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>Whether it makes the workspace degraded: everything but a note does.</summary>
	public bool Degrades => Effect != HealthEffect.Note;

	/// <summary>
	/// What an acceptance or an acknowledgement has to cover for this reason to be covered: each project it
	/// names, or each subject where it names none.
	/// </summary>
	public IReadOnlyList<string> Keys => Projects.Count > 0 ? Projects : Subjects.Count > 0 ? Subjects : [string.Empty];
}
