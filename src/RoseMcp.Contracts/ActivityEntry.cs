namespace RoseMcp.Contracts;

/// <summary>
/// One line of the activity record: a tool call a client made, with who made it, what it was aimed at, how it
/// ended, what it was sent and what it answered, or a broker starting.
/// <para>
/// The record is what a window reads to say what each agent did to a workspace, and it outlives the broker
/// that wrote it: kept on disk, one file per broker run, for as long as the retention setting says. So an entry
/// names everything about its call by value, as it stood when the call ended, rather than by a reference into
/// state a restart forgets.
/// </para>
/// <para>
/// Arguments and responses are kept as they were sent and answered, source and all, with nothing redacted:
/// the record is as private as the solution it describes and the logs beside it, and a redaction that guessed
/// at what a secret looks like would hide code a person needed to read while letting through the secret it
/// did not recognise. What is cut is cut for length alone, and says how long it really was.
/// </para>
/// </summary>
public sealed record ActivityEntry
{
	/// <summary>Unique across every broker run, so a window can ask for this entry again by it.</summary>
	public required string Id { get; init; }

	/// <summary>Whether this is a call or a broker starting.</summary>
	public required ActivityEntryKind Kind { get; init; }

	/// <summary>The broker run that wrote the entry: the instance id it gives relays, new every time a broker starts.</summary>
	public required string Broker { get; init; }

	/// <summary>When the call arrived, or when the broker started.</summary>
	public required DateTimeOffset StartedUtc { get; init; }

	/// <summary>When the call was answered; absent on a call still running and on a broker starting.</summary>
	public DateTimeOffset? FinishedUtc { get; init; }

	/// <summary>On a broker starting, the build it is, as its handshake names it.</summary>
	public string? BrokerBuild { get; init; }

	/// <summary>On a broker starting, its process.</summary>
	public int? BrokerProcessId { get; init; }

	/// <summary>The id the call carried through every process it crossed, which every log line written for it carries too.</summary>
	public string? CorrelationId { get; init; }

	/// <summary>The client's row in the broker's table of clients, <see cref="ClientSummary.Id"/>, where it had one when the call ended.</summary>
	public string? ClientId { get; init; }

	/// <summary>
	/// The client, as a hash of what the broker knows it by: the session a relay names, the transport's session,
	/// or the one unnamed client. A relay outlives a broker restart and keeps its session's name, so this is the
	/// same before and after one where <see cref="ClientId"/> is not; it is a hash because the name itself scopes
	/// what a session acknowledges and no answer carries it.
	/// </summary>
	public string? ClientKey { get; init; }

	/// <summary>The client's own name from its <c>clientInfo</c>, as the broker's table had it.</summary>
	public string? ClientName { get; init; }

	/// <summary>The label a relay's session was started with, as the broker's table had it.</summary>
	public string? ClientLabel { get; init; }

	/// <summary>The directory the calling session works in, as its relay sent it; absent where it sent none.</summary>
	public string? Origin { get; init; }

	/// <summary>The solution the call was routed to, absolute; absent for a call no workspace answered.</summary>
	public string? Workspace { get; init; }

	/// <summary>The short key of <see cref="Workspace"/>.</summary>
	public string? WorkspaceKey { get; init; }

	/// <summary>The debug session the call was routed to; absent for a call that reached none.</summary>
	public string? DebugSession { get; init; }

	/// <summary>The tool called, by the name the client called it by.</summary>
	public string? Tool { get; init; }

	/// <summary>What the call was aimed at in a few words -- a file and line, a symbol, a breakpoint -- or absent for the whole workspace.</summary>
	public string? Target { get; init; }

	/// <summary>How the call ended, or <see cref="ActivityOutcome.Running"/> for one still running; absent on a broker starting.</summary>
	public ActivityOutcome? Outcome { get; init; }

	/// <summary>What the caller was told went wrong, on a call that failed.</summary>
	public string? Error { get; init; }

	/// <summary>
	/// How long <see cref="Error"/> really is, where it was cut: in a listing, to a short excerpt, and in the entry read
	/// by its id, past what the record keeps of an error. Absent otherwise.
	/// </summary>
	public int? ErrorFullLength { get; init; }

	/// <summary>The workspace revision the answer was read at, where it named one.</summary>
	public long? Revision { get; init; }

	/// <summary>On a write, whether it was applied rather than previewed.</summary>
	public bool? Applied { get; init; }

	/// <summary>The arguments as the client sent them, in JSON; a listing leaves them out, and the entry read by its id has them.</summary>
	public string? Arguments { get; init; }

	/// <summary>How long <see cref="Arguments"/> really is, where it was cut; absent otherwise.</summary>
	public int? ArgumentsFullLength { get; init; }

	/// <summary>
	/// What the client was answered: the structured result in JSON where there was one, its text otherwise. A
	/// listing leaves it out, and the entry read by its id has it.
	/// </summary>
	public string? Response { get; init; }

	/// <summary>How long <see cref="Response"/> really is, where it was cut; absent otherwise.</summary>
	public int? ResponseFullLength { get; init; }

	/// <summary>Each file a write changed, or would have, with its diff.</summary>
	public IReadOnlyList<ActivityFileChange> Changes { get; init; } = [];

	/// <summary>What a reader should know about the entry itself, such as a response cut for its length.</summary>
	public IReadOnlyList<string> Notes { get; init; } = [];
}
