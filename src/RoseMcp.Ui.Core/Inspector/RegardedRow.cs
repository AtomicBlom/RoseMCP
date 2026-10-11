using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// The row a person selected last in the Inspector, which the Ask shortcut asks about.
/// <para>
/// Held as the way to make its subject rather than as a subject made when it was clicked: a local clicked at one stop
/// and asked about at the next would otherwise send the agent a frame index and value path that name something else
/// now. The subject is made when the shortcut fires, through whatever owns the row -- the stop on screen for a
/// thread, frame or value, the watch for a watch -- which answers null once the row no longer stands for anything.
/// </para>
/// </summary>
public sealed class RegardedRow
{
	private Func<NoteSubject?>? _subject;

	/// <summary>Notes the row selected last, by what makes its subject now.</summary>
	public void Regard(Func<NoteSubject?> subject) => _subject = subject;

	/// <summary>Forgets the row, for a window that is about another session or none.</summary>
	public void Forget() => _subject = null;

	/// <summary>What a note about the row is about now, or null where nothing was selected or the row is gone.</summary>
	public NoteSubject? Subject() => _subject?.Invoke();
}
