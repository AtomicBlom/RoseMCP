using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>Which declaration to write, and what to write into it.</summary>
public sealed record MemberEditRequest
{
	public required MemberEditKind Kind { get; init; }

	/// <summary>
	/// The member to write over, or the type to add to. Named rather than pointed at: a line and
	/// column has to be found by reading the file, and is wrong the moment an earlier edit lands.
	/// </summary>
	public required string Symbol { get; init; }

	/// <summary>
	/// The C# to write. A whole declaration for <see cref="MemberEditKind.Replace"/> and
	/// <see cref="MemberEditKind.Add"/>; statements, a block, or <c>=&gt; expression;</c> for
	/// <see cref="MemberEditKind.ReplaceBody"/>. Empty for <see cref="MemberEditKind.Delete"/>,
	/// which writes nothing.
	/// </summary>
	public string Code { get; init; } = string.Empty;

	/// <summary>
	/// Code to find inside the body, for a change too small to be worth re-emitting the whole thing
	/// for. Matched on the token stream, so indentation and line endings cannot cause a miss, and
	/// only inside the one member the name resolved to. Zero matches or several is a refusal.
	/// </summary>
	public string? Find { get; init; }

	/// <summary>What to put in place of <see cref="Find"/>. Empty removes the matched code.</summary>
	public string? Replace { get; init; }

	/// <summary>
	/// Match the body's text rather than its tokens, so <see cref="Find"/> may lie inside a comment or
	/// a string.
	/// <para>
	/// The token stream is the right unit for code and cannot see any of this: a comment is trivia, and
	/// the inside of a literal is one token however many words it holds. Without it the four kinds of
	/// text with no tool at all -- a <c>//</c> comment, the body of a string constant, a sentence inside
	/// a tool description, the text in an attribute argument -- are reachable only by re-emitting the
	/// whole member, which is what sends a caller back to a text editor.
	/// </para>
	/// </summary>
	public bool IncludeTrivia { get; init; }

	/// <summary>
	/// Where to insert <see cref="Code"/> instead of replacing the body: the top of the block, or the
	/// end of it -- which means before a closing return or throw, since anything after one is
	/// unreachable.
	/// </summary>
	public BodyPosition? Position { get; init; }

	/// <summary>
	/// Namespaces the written code needs imported, ensured in the same file and the same call.
	/// <para>
	/// In the same call because that is the whole point: the need for an import is discovered at the
	/// moment the member is written, and a second round trip to add one is the round trip these
	/// tools exist to remove. One already in scope -- from this file, a global using, an implicit
	/// using, or the namespace the file is in -- is reported and not added, since adding it is
	/// IDE0005 and that is a build error where the analyzers are turned up.
	/// </para>
	/// </summary>
	public IReadOnlyList<string> Usings { get; init; } = [];

	/// <summary>
	/// Work out what would import the names the edit leaves unresolved, and add the ones with a
	/// single answer.
	/// <para>
	/// On by default. The compilation that finds those names has just been built to say what the
	/// edit broke, so the search is a lookup rather than work, and it runs only where something
	/// failed to bind -- an edit whose imports were right or unneeded pays nothing. What is not
	/// added is a name with more than one candidate: the wrong import compiles and binds to the
	/// wrong type, so those come back as a choice.
	/// </para>
	/// </summary>
	public bool ResolveUsings { get; init; } = true;

	/// <summary>
	/// Which file, when the name alone does not settle it -- a partial type, or a partial member.
	/// Also the workspace hint the broker ranks, being the one argument here that names a path.
	/// </summary>
	public string? FilePath { get; init; }

	/// <summary>
	/// Put the new member after this one, by name. Adding only. Placement is worth controlling
	/// because a member's neighbours are how a reader finds it, and appending to the end of a
	/// several-hundred-line type puts a private helper below the public surface it serves.
	/// </summary>
	public string? After { get; init; }

	/// <summary>Put the new member before this one, by name. Adding only.</summary>
	public string? Before { get; init; }

	/// <summary>False returns the diff without touching disk.</summary>
	public bool Apply { get; init; } = true;

	/// <summary>
	/// Compile the projects holding the file afterwards and report what the edit broke. On by
	/// default: it is the whole reason this is one call rather than two, and it costs a warm
	/// compilation rather than a build.
	/// </summary>
	public bool Verify { get; init; } = true;

	/// <summary>
	/// How much to compile. <see cref="VerifyScope.Auto"/> reads it off the edit -- the file's own
	/// projects for a body change or an effectively private member, their dependents otherwise --
	/// which is the only setting that is right without the caller working out what a member's
	/// accessibility implies about who breaks.
	/// </summary>
	public VerifyScope VerifyScope { get; init; } = VerifyScope.Auto;

	/// <summary>
	/// Fail rather than apply if the workspace has moved past this revision. Matters when more than
	/// one client shares a broker in http mode.
	/// </summary>
	public long? ExpectedRevision { get; init; }
}
