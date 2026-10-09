namespace RoseMcp.Contracts;

/// <summary>
/// One file a write changed, or would change, and where in it.
/// <para>
/// Where rather than what, because what an edit wrote is almost always what its caller sent: reading a
/// member back in a diff teaches the caller nothing it did not compose. The facts the writer owns are
/// where the change landed and what it normalised on the way, and each is a few characters here. The
/// diff itself is on a preview, and on an applied write that asks for it.
/// </para>
/// </summary>
public sealed record ChangedFile
{
	/// <summary>
	/// The file, relative to the calling session's directory where it lies under it, and absolute where
	/// it does not or the broker does not know that directory. That is the directory a relative path
	/// sent back is measured from, with a <c>workspaceKey</c> or without one, so it names the same file
	/// however it returns.
	/// </summary>
	public required string FilePath { get; init; }

	/// <summary>
	/// The lines that changed, numbered as the file now reads: <c>174-200</c>, or several ranges as
	/// <c>3, 174-200</c>. A removal is the line after where it was. Absent where no line's content
	/// changed, which is a write that only retyped line endings.
	/// </summary>
	public string? Lines { get; init; }

	/// <summary>True where the write creates the file; absent otherwise.</summary>
	public bool? Created { get; init; }

	/// <summary>
	/// What the writer made consistent that is not line content, such as <c>34 line endings to
	/// CRLF</c>. A terminator is not part of any line, so this is the one change a diff cannot show.
	/// Absent where nothing was.
	/// </summary>
	public string? Normalised { get; init; }
}
