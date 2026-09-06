using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Where a new file goes and what is in it.</summary>
public sealed record AddFileRequest
{
	/// <summary>
	/// Where the file goes. Path-addressed rather than named by namespace, because a path is what a
	/// caller has and because deriving one from a namespace is the folder convention run backwards,
	/// which is exact in most repositories and silently wrong in the rest.
	/// </summary>
	public required string FilePath { get; init; }

	/// <summary>
	/// The C#. A whole file, or just the declarations -- a namespace is added where the code omits
	/// one. It parses as a compilation unit rather than inside a container, so a namespace
	/// declaration and a file-level using are both legal input.
	/// </summary>
	public required string Code { get; init; }

	/// <summary>Namespaces to import on top of whatever the code needs.</summary>
	public IReadOnlyList<string> Usings { get; init; } = [];

	/// <summary>
	/// Which project compiles it, where the path is inside more than one project's directory. Named
	/// rather than guessed, and refused rather than picked.
	/// </summary>
	public string? Project { get; init; }

	/// <summary>
	/// Work out the namespaces the code needs and add the ones with a single candidate. On by
	/// default: a new file needs imports by definition, and reporting them for the caller to add in
	/// a second call is the round trip these tools exist to remove.
	/// </summary>
	public bool ResolveUsings { get; init; } = true;

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>Compile afterwards and report what the new file broke.</summary>
	public bool Verify { get; init; } = true;

	/// <summary>How much to compile. See <see cref="VerifyScope"/>.</summary>
	public VerifyScope VerifyScope { get; init; } = VerifyScope.Auto;

	/// <summary>Fail rather than apply if the workspace has moved past this revision.</summary>
	public long? ExpectedRevision { get; init; }
}
