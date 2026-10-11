using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.Ui.Core.Views;

/// <summary>What the Agents view knows besides the clients and their calls, to say what each agent is using.</summary>
/// <param name="Workspaces">The workspaces the broker holds, to say whether one an agent used is ready.</param>
/// <param name="Sessions">The debug sessions, to name the app an agent is debugging.</param>
public sealed record AgentContext(IReadOnlyList<OperatorWorkspace> Workspaces, IReadOnlyList<LiveAppSessionSummary> Sessions)
{
	/// <summary>Nothing known yet.</summary>
	public static AgentContext Empty { get; } = new([], []);
}

/// <summary>What a chip on an agent's card names.</summary>
public enum UsingKind
{
	/// <summary>A solution's workspace, which opens the Workspaces view.</summary>
	Workspace,

	/// <summary>A debugged app, which opens its Inspector window.</summary>
	App,
}

/// <summary>
/// One thing an agent is using, as a chip on its card: a workspace it called into, or an app it is debugging, with
/// what state that is in now. What it is using is read from its calls rather than from any field of the client,
/// because the calls are the only place the broker records it.
/// </summary>
/// <param name="Kind">A workspace or an app.</param>
/// <param name="Key">The solution path or the session id: what opening the chip goes to.</param>
/// <param name="Label">The solution's file name, or the app's description.</param>
/// <param name="State">Its state now, in a word or two; empty where it is not held any longer.</param>
public sealed record UsingChip(UsingKind Kind, string Key, string Label, string State)
{
	/// <summary>The chip's one line.</summary>
	public string Text => State.Length > 0 ? $"{Label}{Format.Separator}{State}" : Label;

	public bool IsWorkspace => Kind == UsingKind.Workspace;

	/// <summary>
	/// The workspaces and apps <paramref name="entries"/> reached, newest first, each once, with the state
	/// <paramref name="context"/> gives it. A solution is one chip however its path is spelled.
	/// </summary>
	public static IReadOnlyList<UsingChip> Of(IEnumerable<ActivityEntry> entries, AgentContext context)
	{
		var chips = new List<UsingChip>();
		var workspaces = new HashSet<string>(PathCasing.Comparer);
		var sessions = new HashSet<string>(StringComparer.Ordinal);

		foreach (var entry in entries.Where(entry => entry.Kind == ActivityEntryKind.Call).OrderByDescending(entry => entry.StartedUtc))
		{
			if (entry.Workspace is { Length: > 0 } workspace && workspaces.Add(workspace))
			{
				chips.Add(ForWorkspace(workspace, context));
			}

			if (entry.DebugSession is { Length: > 0 } session && sessions.Add(session))
			{
				chips.Add(ForSession(session, context));
			}
		}

		return chips;
	}

	private static UsingChip ForWorkspace(string workspace, AgentContext context)
	{
		// The broker's spelling where it holds the workspace: a call may have named the path in any case, and the
		// chip names the solution as its row in the Workspaces view does.
		var row = context.Workspaces.FirstOrDefault(row => row.Summary.Workspace is { } path && PathCasing.Comparer.Equals(path, workspace));
		if (row is null) return new UsingChip(UsingKind.Workspace, workspace, Format.FileLine(workspace, line: null), "closed");

		return new UsingChip(UsingKind.Workspace, row.Summary.Workspace!, row.Summary.DisplayName, WorkspaceRow.StateWord(row));
	}

	private static UsingChip ForSession(string sessionId, AgentContext context)
	{
		var session = context.Sessions.FirstOrDefault(session => session.SessionId == sessionId);
		if (session is null) return new UsingChip(UsingKind.App, sessionId, $"debug session {sessionId}", "ended");

		var label = session.TargetProcessId is { } pid ? $"{session.TargetDescription}{Format.Separator}{Format.Pid(pid)}" : session.TargetDescription;

		return new UsingChip(UsingKind.App, sessionId, label, SessionRow.DescribeState(session).ToLowerInvariant());
	}
}
