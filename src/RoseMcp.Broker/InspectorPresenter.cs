using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.Settings;

namespace RoseMcp.Broker;

/// <summary>
/// Opens the inspector on a session that has just started, when something asked for it.
/// <para>
/// A seam rather than a call to <see cref="InspectorLauncher"/> because only the host knows whether
/// there is anything to open an inspector <em>against</em>. The operator API is what the inspector
/// talks to, and a bare stdio broker has neither a port nor a token -- so in that host this cannot
/// be honoured at all, and the honest thing is to say so rather than quietly do nothing.
/// </para>
/// </summary>
public interface IInspectorPresenter
{
	/// <summary>Whether this host can open an inspector at all.</summary>
	bool CanShow { get; }

	/// <summary>
	/// Why not, for a caller that asked and cannot have it. Null when <see cref="CanShow"/>.
	/// </summary>
	string? Obstacle { get; }

	/// <summary>
	/// Opens the inspector on a session, or reports why it could not. Never throws: failing to
	/// open a window must not fail the attach that asked for it, because the session is already
	/// running and the caller's real work depends on it.
	/// </summary>
	/// <returns>Null when the inspector was started, else the reason it was not.</returns>
	string? Show(string sessionId, int? targetProcessId);
}

/// <summary>
/// What a host with no operator surface registers. Every question answers "no, and here is why".
/// </summary>
public sealed class NoInspector(string obstacle) : IInspectorPresenter
{
	/// <summary>What a stdio broker says: there is no endpoint for an inspector to talk to.</summary>
	public static NoInspector WithoutAnEndpoint { get; } = new(
		"This broker serves one client over stdio and has no http endpoint, so there is nothing for an "
			+ "inspector to connect to. Run RoseMcp.Tray, which hosts one, and the inspector opens against it.");

	public bool CanShow => false;

	public string? Obstacle { get; } = obstacle;

	public string? Show(string sessionId, int? targetProcessId) => Obstacle;
}

/// <summary>
/// Opens the inspector against this host's own operator endpoint.
/// <para>
/// It hands over the target's process id as well as the session, because the inspector is one
/// window per debugged process and keys its single instance on that. Asking twice for one process
/// therefore focuses the window that is open rather than starting a second.
/// </para>
/// </summary>
public sealed class OperatorInspector(
	string host,
	int port,
	OperatorToken token,
	ILogger<OperatorInspector> logger,
	string? inspectorPath = null) : IInspectorPresenter
{
	public bool CanShow => InspectorLauncher.ResolvePath(inspectorPath) is not null;

	public string? Obstacle => CanShow
		? null
		: "No inspector is installed. A published install has it in 'inspector' beside the tray's own "
			+ "folder; from source, build RoseMcp.Inspector, or set ROSEMCP_INSPECTOR to its executable.";

	public string? Show(string sessionId, int? targetProcessId)
	{
		try
		{
			InspectorLauncher.Launch(host, port, token, sessionId, inspectorPath, targetProcessId);
			return null;
		}
		catch (Exception exception)
		{
			// Logged and reported, never thrown. The session this was asked for is already running,
			// and failing the attach over a window would throw away the work that mattered.
			logger.LogWarning(exception, "Could not open the inspector on session {Session}.", sessionId);
			return exception.Message;
		}
	}
}

/// <summary>
/// Whether a request and the machine's preference add up to opening a window.
/// <para>
/// Separated from both the tools that ask and the hosts that can so it can be read in one place
/// and tested without either. <see cref="InspectorVisibility.UserPreference"/> is the only value
/// that consults the settings file; the other two are the caller overruling it deliberately.
/// </para>
/// </summary>
public static class InspectorRequest
{
	public static bool Wanted(InspectorVisibility asked, RoseSettings settings) => asked switch
	{
		InspectorVisibility.Always => true,
		InspectorVisibility.Never => false,
		_ => settings.ShowInspectorOnAttach,
	};
}
