namespace RoseMcp.Broker;

/// <summary>
/// Which MCP session made this call, for the length of that call.
/// <para>
/// A live-app session is a debugger attached to somebody's running program, and the broker is a
/// singleton every connection shares. Without an owner recorded against each one, any client of an
/// http broker can read another client's captured exceptions and log output, set breakpoints in its
/// target, evaluate expressions inside it, and detach it -- by guessing an eight-character id, or by
/// reading GET /admin/sessions, which lists them.
/// </para>
/// <para>
/// Ambient rather than a tool parameter, for the reason <see cref="CallOrigin"/> is: threading it
/// through every tool method means the tool added next is the one that forgets, and a debugging
/// surface of twenty-two tools is exactly where that happens. Null is the honest value for a stdio
/// broker, which has one session for the life of the process, so every call in it owns everything
/// it started and nothing else can reach the port at all.
/// </para>
/// </summary>
public static class CallSession
{
	private static readonly AsyncLocal<string?> Ambient = new();

	/// <summary>The calling session's id, or null when the transport has no notion of one.</summary>
	public static string? Id => Ambient.Value;

	/// <summary>
	/// Sets it for the current call. Each MCP request runs on its own execution context, so this is
	/// scoped to one request and cannot leak into a concurrent one; it is restored anyway, because a
	/// filter that leaves ambient state behind is a filter nobody can reason about.
	/// </summary>
	public static IDisposable Use(string? id) => new Scope(id);

	private sealed class Scope : IDisposable
	{
		private readonly string? _previous;

		public Scope(string? id)
		{
			_previous = Ambient.Value;
			Ambient.Value = string.IsNullOrWhiteSpace(id) ? null : id;
		}

		public void Dispose() => Ambient.Value = _previous;
	}
}
