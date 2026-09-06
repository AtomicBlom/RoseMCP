namespace RoseMcp.Contracts;

/// <summary>What creating a file did, and everything a caller would otherwise have to check.</summary>
public sealed record AddFileResult : WorkspaceMutationResult
{
	public required long Revision { get; init; }

	public required string FilePath { get; init; }

	/// <summary>
	/// The project that compiles it, chosen by which project's directory contains the path. Reported
	/// because it is a decision the caller did not make and cannot see from the file.
	/// </summary>
	public required string Project { get; init; }

	/// <summary>
	/// The namespace the file declares, whether the code said it or it was derived from the folder.
	/// A namespace that does not match the folder is IDE0130, so which one this is matters.
	/// </summary>
	public required string Namespace { get; init; }

	/// <summary>The types the file declares.</summary>
	public required IReadOnlyList<string> Types { get; init; }

	/// <summary>False when this was a preview; nothing was written.</summary>
	public required bool Applied { get; init; }

	/// <summary>
	/// False when the owning project lists the files it compiles rather than globbing them, so the
	/// file exists and the build cannot see it until the project names it. Not an error and not a
	/// notice: it is the one fact about a new file that nothing else would reveal, because a
	/// compilation that does not include it reports no problems with it at all.
	/// </summary>
	public required bool InTheBuild { get; init; }

	/// <summary>Namespaces imported to make the code resolve, and which name wanted each.</summary>
	public IReadOnlyList<string> ImportsAdded { get; init; } = [];

	/// <summary>
	/// Names that could be imported from more than one namespace, with the candidates. Nothing was
	/// added for these: the wrong import compiles and binds to the wrong type, which is the failure
	/// with no symptom at all.
	/// </summary>
	public IReadOnlyList<string> ImportsAmbiguous { get; init; } = [];

	/// <summary>Names nothing in scope could resolve, with why not.</summary>
	public IReadOnlyList<string> Unresolved { get; init; } = [];

	public required string Diff { get; init; }

	/// <summary>Errors the new file introduced, whoever they are in.</summary>
	public IReadOnlyList<DiagnosticEntry> IntroducedDiagnostics { get; init; } = [];

	public bool Verified { get; init; }

	public IReadOnlyList<string> ProjectsChecked { get; init; } = [];
}
