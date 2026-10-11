namespace RoseMcp.Server;

/// <summary>A debug session this session had open, as the relay last saw it.</summary>
/// <param name="SessionId">The session's id, as the caller would pass it.</param>
/// <param name="Target">What it was debugging, as the broker described it.</param>
public sealed record DebugSessionSeen(string SessionId, string Target);
