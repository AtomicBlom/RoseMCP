using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>What absorbing files that appeared on disk did.</summary>
public sealed record NewFileResult
{
	public required Solution Solution { get; init; }

	/// <summary>Paths added to the compilation, with the project each joined.</summary>
	public required IReadOnlyList<string> Added { get; init; }

	/// <summary>
	/// A project file, a build file, or an .editorconfig appeared. None of those can be patched into
	/// a snapshot -- they change how projects evaluate rather than what is in them -- so the caller
	/// reloads.
	/// </summary>
	public required bool StructuralChange { get; init; }

	/// <summary>
	/// Source files that are inside a project's directory but were not added, because that project
	/// lists the files it compiles instead of globbing them. Said out loud rather than dropped: the
	/// file exists, it looks compiled, and it is not -- which is a thing nobody would guess at from
	/// an answer that simply did not mention it.
	/// </summary>
	public required IReadOnlyList<string> NotInTheBuild { get; init; }

	public bool AnythingChanged => Added.Count > 0 || StructuralChange;
}
