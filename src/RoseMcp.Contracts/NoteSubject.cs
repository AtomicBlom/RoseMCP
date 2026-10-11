namespace RoseMcp.Contracts;

/// <summary>
/// What a note to an agent is about, as a reference the agent can look up and a line both it and the person read.
/// <para>
/// One record for every kind rather than one per kind, because a note travels through one route, one store and
/// one notice, and a kind's references are a handful of optional fields: <see cref="Kind"/> says which of them
/// mean anything, and the broker keeps only those, refusing a subject that leaves out one its kind needs.
/// <see cref="Readable"/> is what a person sees in the window and what the agent is told before the references,
/// so it says the thing in words -- <c>_pen.Width = 2.5 in InkToolbarViewModel.ApplyWidth</c> -- where the
/// references say it exactly.
/// </para>
/// </summary>
public sealed record NoteSubject
{
	/// <summary>Which kind of thing it is, and so which of the references below it carries.</summary>
	public required NoteSubjectKind Kind { get; init; }

	/// <summary>The thing in words, one line: what the person saw, as they would say it.</summary>
	public required string Readable { get; init; }

	/// <summary>The debug session it is in, for a value, a thread, a frame or an element.</summary>
	public string? Session { get; init; }

	/// <summary>The thread, for a thread, and for a value or a frame where it was read on a thread other than the stopped one.</summary>
	public int? ThreadId { get; init; }

	/// <summary>The frame, from the top of the stack: for a frame, and for a value read in one.</summary>
	public int? FrameIndex { get; init; }

	/// <summary>The value's path, as <c>rose_debug_evaluate</c> takes one: <c>this._pen.Width</c>, <c>local:2.Inner</c>.</summary>
	public string? Path { get; init; }

	/// <summary>The element's handle in the live visual tree, as <c>rose_xaml_properties</c> takes one.</summary>
	public string? Element { get; init; }

	/// <summary>The solution whose health the reason is in.</summary>
	public string? Workspace { get; init; }

	/// <summary>The reason's kind, one of <see cref="ReasonKinds"/>.</summary>
	public string? ReasonKind { get; init; }

	/// <summary>The reason's id in the workspace's status, as <c>rose_workspace_acknowledge</c> takes it, where the person had one.</summary>
	public string? Reason { get; init; }

	/// <summary>The activity record's id for the call.</summary>
	public string? ActivityEntry { get; init; }
}
