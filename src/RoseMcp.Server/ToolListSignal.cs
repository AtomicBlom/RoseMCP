using ModelContextProtocol.Server;

namespace RoseMcp.Server;

/// <summary>
/// How the relay sends <c>notifications/tools/list_changed</c>: an empty tool collection given to
/// its server, whose change event the SDK turns into the notification.
/// <para>
/// Through the SDK rather than by sending the notification directly, because how it has to be sent
/// depends on the protocol revision the client chose. A client on the 2026-07-28 revision -- Claude
/// Code is one -- asks for change notifications on a <c>subscriptions/listen</c> stream, and must
/// receive them tagged with that subscription and only if it asked; an older client gets one
/// broadcast on the session. The SDK's fan-out for a changed collection does both, and a
/// hand-sent notification would be right for one of them.
/// </para>
/// <para>
/// It never holds a tool. The relay declares none of its own, and the SDK lists a collection's tools
/// ahead of the handler's, so one added here would appear beside every tool the broker has.
/// </para>
/// </summary>
public sealed class ToolListSignal : McpServerPrimitiveCollection<McpServerTool>
{
	/// <summary>Tells the client the tool list changed, if a server is listening; does nothing otherwise.</summary>
	public void Raise() => RaiseChanged();
}
