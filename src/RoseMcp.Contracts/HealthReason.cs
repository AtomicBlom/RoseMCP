namespace RoseMcp.Contracts;

/// <summary>
/// One reason in a workspace's health, with the shape a person or an agent can act on: what kind it is,
/// what it does to answers, which projects it covers, when it was first seen, and whether it has been
/// accepted or acknowledged.
/// <para>
/// Beside the plain lists rather than in place of them. <see cref="WorkspaceStatusReport.DegradedReasons"/>
/// and <see cref="WorkspaceStatusReport.Notices"/> still carry each reason in full, its fix included, and
/// this carries <see cref="Line"/> -- the opening sentence a read would quote -- rather than the text again,
/// since a degraded solution's reasons are the longest part of its status and saying them twice is the
/// cost a status answer can least afford.
/// </para>
/// <para>
/// The worker fills the shape; the broker fills <see cref="FirstSeenUtc"/>, <see cref="Accepted"/> and
/// <see cref="AcknowledgedUtc"/>, since the broker is what remembers across loads and knows which session
/// is asking.
/// </para>
/// </summary>
public sealed record HealthReason
{
	/// <summary>
	/// What <c>rose_workspace_acknowledge</c> takes to name this reason: its kind, and its project where it
	/// covers one, as <c>project-not-loaded:Core</c>. Unique within one answer, so a second reason of the same
	/// kind and project carries a suffix.
	/// </summary>
	public required string Id { get; init; }

	/// <summary>Which cause this is, one of <see cref="ReasonKinds"/>. The same at every load, whatever the wording.</summary>
	public required string Kind { get; init; }

	/// <summary>What it does to the answers the workspace gives.</summary>
	public required HealthEffect Effect { get; init; }

	/// <summary>
	/// The projects it covers, by name. Empty for a reason about the whole solution or the worker, whose
	/// acceptance is keyed on its <see cref="Subjects"/> instead.
	/// </summary>
	public IReadOnlyList<string> Projects { get; init; } = [];

	/// <summary>
	/// What a reason naming no project is about -- the assemblies that would not load, the platform chosen, the
	/// restore's error codes -- which an acceptance is keyed on in place of a project, so a different assembly
	/// failing later is a reason nobody accepted. Empty where the reason names projects.
	/// </summary>
	public IReadOnlyList<string> Subjects { get; init; } = [];

	/// <summary>Rose's line: the reason's opening sentence, cut to a ceiling, as a read from a degraded workspace quotes it.</summary>
	public required string Line { get; init; }

	/// <summary>When this broker first saw this kind for these projects, across loads and restarts. Null until the broker has said.</summary>
	public DateTime? FirstSeenUtc { get; init; }

	/// <summary>The person's acceptance, where every project it covers has one for this kind. Null otherwise.</summary>
	public ReasonAcceptance? Accepted { get; init; }

	/// <summary>When the calling session acknowledged it, so its reads stop saying it. Null where it has not.</summary>
	public DateTime? AcknowledgedUtc { get; init; }
}
