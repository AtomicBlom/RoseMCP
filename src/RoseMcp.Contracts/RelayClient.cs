namespace RoseMcp.Contracts;

/// <summary>
/// Who a relay serves, as it tells the broker at <see cref="RelayProtocol.ClientRoute"/>: the facts only a
/// stdio process standing in front of one agent session can know.
/// <para>
/// Sent once the client has made its first request, because its name and its roots exist only once it has
/// introduced itself; again after every reconnect, because a restarted broker remembers nothing; and again
/// whenever one of them changes. The broker trusts none of it: every field is a claim from some process on
/// this machine, so each is limited in length and stripped of control characters before it is kept.
/// </para>
/// </summary>
public sealed record RelayClient
{
	/// <summary>
	/// The name the relay sends in <c>_meta["rosemcp/session"]</c> on every call, which is what ties the calls
	/// the broker sees to these facts.
	/// </summary>
	public required string Session { get; init; }

	/// <summary>
	/// The directory the client started the relay in. Claude Code starts a stdio server in the session's own
	/// folder, so this names the session even where the client declares no roots.
	/// </summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>The client's roots, from <c>roots/list</c>, as local paths where they are file URIs, in the client's order.</summary>
	public IReadOnlyList<string> Roots { get; init; } = [];

	/// <summary>
	/// The branch checked out where the first root is, or the working directory where there is none; for a
	/// detached head, "detached at" and the commit; null outside a git checkout.
	/// </summary>
	public string? Branch { get; init; }

	/// <summary>The client's process: the one that started the relay, past any shell in between.</summary>
	public int? ClientProcessId { get; init; }

	/// <summary>That process's name, as the operating system gives it.</summary>
	public string? ClientProcessName { get; init; }

	/// <summary>The name the client gave itself in its <c>clientInfo</c>.</summary>
	public string? ClientName { get; init; }

	/// <summary>The version the client gave in its <c>clientInfo</c>.</summary>
	public string? ClientVersion { get; init; }

	/// <summary>
	/// <c>ROSE_AGENT_LABEL</c> from the relay's environment, which whatever starts a session can set -- to the
	/// task it was given, say -- so a person can tell its sessions apart by more than their folders.
	/// </summary>
	public string? Label { get; init; }

	/// <summary>A hash of the tool definitions and instructions the client holds: sixty-four lowercase hex digits.</summary>
	public string? HeldTools { get; init; }

	/// <summary>
	/// Whether what the client holds is what the broker offers, as the relay last read the broker: false
	/// between a restart that changed the tools and the client listing them again.
	/// </summary>
	public bool? ToolsCurrent { get; init; }
}
