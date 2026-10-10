using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// What the first result after the relay reconnects says, in one line: that RoseMCP restarted and
/// which build it is now, that solutions load again, which debug sessions ended, and what changed
/// in the tools.
/// <para>
/// A line on a result rather than anything at the protocol level, because a restart with the same
/// tools sends the client nothing else -- and the agent still needs to know that its next call
/// pays for a solution load, that the debugger it was driving is gone, and which build answers now.
/// Said once, on whichever result comes first, since a line that repeats on every call is a line
/// that is read past.
/// </para>
/// <para>
/// It names only what the relay can honestly know. The build is the new broker's handshake; the
/// debug sessions are the ones this session's own calls had open when the relay last looked, which
/// is after every call that starts or ends one -- a session whose host died between those looks is
/// still named, as one this session had open, which it was.
/// </para>
/// </summary>
public sealed record RestartNotice
{
	/// <summary>How many names of one kind are spelled out before the rest are counted.</summary>
	private const int NamedAtMost = 5;

	/// <summary>The build the relay is connected to now.</summary>
	public required BuildIdentity Build { get; init; }

	/// <summary>
	/// Whether the broker is a new instance. False when the relay reconnected to the broker it had --
	/// which leaves solutions loaded but still costs the debug sessions, since each belongs to the
	/// connection that started it.
	/// </summary>
	public required bool Restarted { get; init; }

	/// <summary>The debug sessions this session had open on the broker it lost.</summary>
	public IReadOnlyList<DebugSessionSeen> EndedSessions { get; init; } = [];

	/// <summary>What the client holds that the new broker defines differently, or null where its tools could not be read.</summary>
	public ToolSurfaceChange? Tools { get; init; }

	/// <summary>The broker's relay protocol where it is not this relay's, which no reconnect can mend.</summary>
	public int? BrokerRelayProtocol { get; init; }

	/// <summary>
	/// This notice with a later one folded in, for a second reconnect before any result carried the
	/// first: the later build and protocol, every session that ended in either, and the later tool
	/// change unless it found nothing new -- it is measured against what the client last listed, so
	/// it already covers the earlier one unless the client listed in between.
	/// </summary>
	public RestartNotice Then(RestartNotice later) => new()
	{
		Build = later.Build,
		Restarted = Restarted || later.Restarted,
		EndedSessions = [.. EndedSessions.Concat(later.EndedSessions).DistinctBy(session => session.SessionId, StringComparer.Ordinal)],
		Tools = later.Tools is { IsEmpty: true } ? Tools : later.Tools,
		BrokerRelayProtocol = later.BrokerRelayProtocol,
	};

	/// <summary>The line itself.</summary>
	public string Describe()
	{
		var clauses = new List<string>
		{
			Restarted
				? $"RoseMCP restarted and is now build {Build.Describe()}"
				: $"The connection to RoseMCP (build {Build.Describe()}) was opened again",
		};

		if (Restarted) clauses.Add("solutions it had loaded load again on the first call that needs them");

		if (EndedSessions.Count > 0)
		{
			var sessions = Named(EndedSessions.Select(session => $"{session.SessionId} ({session.Target})").ToList());

			clauses.Add(Restarted
				? $"the debug sessions this session had open ended with it: {sessions}"
				: $"the debug sessions this session had open belong to the old connection and cannot be reached from this one: {sessions}");
		}

		clauses.AddRange(ToolClauses());

		if (BrokerRelayProtocol is { } theirs)
		{
			clauses.Add(
				$"this session's relay speaks relay protocol {RelayProtocol.Version} and RoseMCP speaks {theirs}, so reconnect "
					+ "the MCP client (/mcp) to start the relay installed with it");
		}

		return string.Join("; ", clauses) + ".";
	}

	private IEnumerable<string> ToolClauses()
	{
		if (Tools is null)
		{
			yield return "its tool list could not be read, so the client was asked to list the tools again";
			yield break;
		}

		if (Tools.ToolsChanged)
		{
			var parts = new List<string>();
			if (Tools.Added.Count > 0) parts.Add($"added {Named(Tools.Added)}");
			if (Tools.Removed.Count > 0) parts.Add($"removed {Named(Tools.Removed)}");
			if (Tools.Changed.Count > 0) parts.Add($"changed {Named(Tools.Changed)}");

			yield return $"its tools changed ({string.Join("; ", parts)}), and the client was asked to list them again";
		}

		if (Tools.InstructionsChanged)
		{
			yield return "its server instructions changed, which a client reads only when it connects, so reconnecting "
				+ "the MCP client (/mcp) picks them up";
		}
	}

	/// <summary>Names joined with commas, the ones past <see cref="NamedAtMost"/> counted rather than listed.</summary>
	private static string Named(IReadOnlyList<string> names) =>
		names.Count <= NamedAtMost
			? string.Join(", ", names)
			: $"{string.Join(", ", names.Take(NamedAtMost))} and {names.Count - NamedAtMost} more";
}
