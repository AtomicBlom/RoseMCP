namespace RoseMcp.Broker;

/// <summary>What an attach or a launch through <see cref="LiveAppSessionManager"/> came to.</summary>
/// <param name="Session">The session debugging the target.</param>
/// <param name="Existing">
/// Whether that session was already open rather than started here: a process has one debugger, so asking to debug one
/// that is being debugged reaches the session that is.
/// </param>
public sealed record LiveAppStart(LiveAppSession Session, bool Existing);
