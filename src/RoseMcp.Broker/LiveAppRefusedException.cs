namespace RoseMcp.Broker;

/// <summary>
/// A debug session could not be started or changed as asked, for a reason in the state of things rather than in the
/// request: the host could not reach its target, the process is already being debugged by somebody else, or the
/// session is not one that can be shared. The message says which, in the words the caller reads.
/// <para>
/// Its own type so the operator API can answer it as a conflict, apart from a malformed request and from a fault;
/// the MCP boundary forwards its message as it forwards any refusal Rose writes.
/// </para>
/// </summary>
public sealed class LiveAppRefusedException(string message) : InvalidOperationException(message);
