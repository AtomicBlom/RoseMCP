using System.Text.Json.Serialization;

namespace RoseMcp.Contracts;

/// <summary>
/// What a note to an agent is about: the thing the person was looking at when they wrote it. Each kind names the
/// references a <see cref="NoteSubject"/> of that kind carries, so the agent can look the thing up rather than
/// trust a description of it.
/// <para>
/// Read and written by name whatever options a serialiser is given, since it travels in an operator request's
/// body, which the framework binds with its own options rather than with <see cref="ContractJson.Options"/>.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteSubjectKind>))]
public enum NoteSubjectKind
{
	/// <summary>A value in a debugged app: <see cref="NoteSubject.Session"/> and <see cref="NoteSubject.Path"/>, with the thread and frame it was read in.</summary>
	DebugValue,

	/// <summary>A thread of a debugged app: <see cref="NoteSubject.Session"/> and <see cref="NoteSubject.ThreadId"/>.</summary>
	Thread,

	/// <summary>A stack frame of a debugged app: <see cref="NoteSubject.Session"/> and <see cref="NoteSubject.FrameIndex"/>, with the thread it is on.</summary>
	Frame,

	/// <summary>An element of a live visual tree: <see cref="NoteSubject.Session"/> and <see cref="NoteSubject.Element"/>.</summary>
	Element,

	/// <summary>A reason in a workspace's health: <see cref="NoteSubject.Workspace"/> and <see cref="NoteSubject.ReasonKind"/>.</summary>
	WorkspaceReason,

	/// <summary>A call the agent made, as the activity record holds it: <see cref="NoteSubject.ActivityEntry"/>.</summary>
	ActivityCall,

	/// <summary>An event a debugged app's session recorded: <see cref="NoteSubject.Session"/> and <see cref="NoteSubject.EventSequence"/>, as <c>rose_debug_events</c> takes it.</summary>
	DebugEvent,

	/// <summary>A breakpoint or tracepoint in a debugged app: <see cref="NoteSubject.Session"/> and <see cref="NoteSubject.Breakpoint"/>.</summary>
	Breakpoint,
}
