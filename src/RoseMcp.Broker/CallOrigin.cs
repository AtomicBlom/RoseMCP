namespace RoseMcp.Broker;

/// <summary>
/// The directory the session that made this call lives in, for the duration of that call.
/// <para>
/// This is the fact the broker was missing, and every workaround built on top of that gap traces
/// back to here. An http broker serves every repository on the machine, so its own
/// <c>Environment.CurrentDirectory</c> -- the tray's install directory, holding no solution at all
/// -- can say nothing about which one a bare call means. Lacking anything better it used to answer
/// from whichever single workspace happened to be loaded, which is not a fact about the question
/// but about what somebody else did earlier. Because that guess was dangerous, the stdio relay grew
/// a pre-emptive resolve to stop calls ever reaching it, and that resolve is what swallowed an
/// explicitly named solution and failed calls for tools that want no solution at all.
/// </para>
/// <para>
/// A stdio session cannot be ambiguous about this: its client chose its working directory. So it
/// sends the directory and the broker draws the conclusion, which is the only arrangement where one
/// ordering decides every call. Relayed and direct sessions then differ by one input rather than by
/// a code path -- the direct case leaves this unset and <see cref="BrokerOptions.DefaultWorkspaceRoot"/>
/// is already its own working directory.
/// </para>
/// <para>
/// Ambient rather than a parameter, and deliberately: threading it through every tool method is the
/// hazard this whole change is removing, since the tool added next is the one that forgets. It
/// arrives out of band in <c>_meta</c> for the same reason -- no tool schema mentions it, so no tool
/// can fail to declare it.
/// </para>
/// </summary>
public static class CallOrigin
{
	/// <summary>
	/// The <c>_meta</c> key carrying it. Namespaced, as MCP asks of metadata that is not its own --
	/// the incoming side of the same field already carries <c>claudecode/toolUseId</c>.
	/// </summary>
	public const string MetaKey = "rosemcp/originDirectory";

	private static readonly AsyncLocal<string?> Ambient = new();

	/// <summary>The calling session's directory, or null when it did not say.</summary>
	public static string? Directory => Ambient.Value;

	/// <summary>
	/// Sets it for the current call. Each MCP request runs on its own execution context, so this is
	/// scoped to one request and cannot leak into a concurrent one; it is restored anyway, because a
	/// filter that leaves ambient state behind is a filter nobody can reason about.
	/// </summary>
	public static IDisposable Use(string? directory) => new Scope(directory);

	private sealed class Scope : IDisposable
	{
		private readonly string? _previous;

		public Scope(string? directory)
		{
			_previous = Ambient.Value;
			Ambient.Value = string.IsNullOrWhiteSpace(directory) ? null : directory;
		}

		public void Dispose() => Ambient.Value = _previous;
	}
}
