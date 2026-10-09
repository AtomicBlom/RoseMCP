namespace RoseMcp.Broker;

/// <summary>A live-app session the manager dropped because its host stopped answering.</summary>
/// <param name="SessionId">The id the session had.</param>
/// <param name="Owner">The MCP session that started it, or null where the transport has none.</param>
/// <param name="Target">What it was debugging, in the words its target was described with.</param>
/// <param name="HostGoneUtc">When the manager found its host gone.</param>
public sealed record DroppedSession(string SessionId, string? Owner, string Target, DateTime HostGoneUtc)
{
	/// <summary>
	/// Why a call naming this session finds nothing, said so the caller knows the id was right and the
	/// session is not coming back.
	/// </summary>
	/// <param name="nowUtc">Now, on the clock <see cref="HostGoneUtc"/> was read from.</param>
	/// <param name="startAgain">What the reader does to debug the target again, in their own surface's terms.</param>
	public string Explain(DateTime nowUtc, string startAgain) =>
		$"Debug session '{SessionId}' for {Target} has ended: its live-app host stopped answering "
			+ $"{WorkerEviction.Duration(nowUtc - HostGoneUtc)} ago, and the debugger went with it, so the session "
			+ $"was dropped and nothing in it can be read. {startAgain}";
}
