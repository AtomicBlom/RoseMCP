using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// What a restart did to the agents connected across it, gathered while their relays reconnect and said
/// once they have settled: how many came back, and whether their tools changed.
/// <para>
/// Only what this broker can see. The relays that reconnect introduce themselves again, and each says
/// whether its client holds this broker's tools; one whose tools changed says it does not until its client
/// lists them again, and then says it does with a different hash. Watching across the settle window is what
/// catches a client that relisted quickly. What the old broker had -- the debug sessions that ended with it,
/// which tools changed by name -- went with that process, so it is not said here; each agent hears it on its
/// own first result.
/// </para>
/// <para>
/// The tray passes only the clients whose relay outlived the broker before this one. A session that first
/// connects in the settle window did not reconnect, and saying it did would announce a restart at sign-in.
/// </para>
/// </summary>
public sealed class RestartReport
{
	/// <summary>
	/// How long after the broker starts the report waits before it says anything. A relay notices a restart on
	/// its watch and reconnects within seconds; this leaves room for a slow one and for its client to relist.
	/// </summary>
	public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(30);

	private readonly Dictionary<string, Seen> _seen = new(StringComparer.Ordinal);
	private HashSet<string> _watching = new(StringComparer.Ordinal);

	/// <summary>
	/// Takes one look at the clients that reconnected. Each call replaces which are connected now, and adds to
	/// what is known of each one's tools.
	/// </summary>
	public void Observe(IEnumerable<ClientSummary> reconnected)
	{
		var watching = new HashSet<string>(StringComparer.Ordinal);

		foreach (var client in reconnected)
		{
			var connected = client.Named && client.Watching == true;
			if (!connected) continue;

			watching.Add(client.Id);

			if (!_seen.TryGetValue(client.Id, out var seen))
			{
				seen = new Seen();
				_seen[client.Id] = seen;
			}

			var relisted = client.HeldTools is { } held && seen.FirstHeld is { } first && !string.Equals(held, first, StringComparison.Ordinal);
			if (client.ToolsCurrent == false || relisted) seen.Changed = true;

			seen.FirstHeld ??= client.HeldTools;
			seen.Current = client.ToolsCurrent;
		}

		_watching = watching;
	}

	/// <summary>
	/// The notification, or null where no agent reconnected, which is a start rather than a restart as far as
	/// any agent is concerned.
	/// </summary>
	/// <param name="build">This build.</param>
	/// <param name="previousCommit">The commit the tray that ran before this one was built from, where known.</param>
	public TrayNotification? Notification(BuildIdentity build, string? previousCommit)
	{
		var agents = _watching.Select(id => _seen[id]).ToList();
		if (agents.Count == 0) return null;

		var now = build.ShortCommit() ?? build.Version;
		var before = previousCommit is { Length: > 7 } full ? full[..7] : previousCommit;
		var moved = before is { Length: > 0 } && build.ShortCommit() is { } current && !string.Equals(before, current, StringComparison.OrdinalIgnoreCase);

		var title = moved ? $"Rose restarted on {now} (was {before})" : $"Rose restarted on {now}";

		var count = agents.Count;
		var changed = agents.Count(agent => agent.Changed);
		var stale = agents.Count(agent => agent.Current == false);
		var unknown = agents.Count(agent => agent.Current is null && !agent.Changed);

		var sentences = new List<string> { $"{Format.Count(count, "agent session")} reconnected." };

		if (changed > 0)
		{
			sentences.Add(count == 1
				? "Its tools changed, and Rose asked it to list them again."
				: changed == count
					? "Tools changed for all of them, and Rose asked them to list them again."
					: $"Tools changed for {changed} of them, and Rose asked {(changed == 1 ? "it" : "them")} to list them again.");

			if (stale > 0) sentences.Add(stale == 1 ? "1 has not listed the new tools yet." : $"{stale} have not listed the new tools yet.");
		}
		else if (unknown == 0)
		{
			sentences.Add(count == 1 ? "Its tools are unchanged." : "Their tools are unchanged.");
		}

		return TrayNotification.Worded(TrayNotificationKind.Restart, title, string.Join(' ', sentences), TrayAction.OpenRose);
	}

	private sealed class Seen
	{
		/// <summary>The hash of the tools the client held when first seen.</summary>
		public string? FirstHeld { get; set; }

		/// <summary>Whether it was ever seen holding tools other than this broker's.</summary>
		public bool Changed { get; set; }

		/// <summary>Whether it holds this broker's tools, as last seen.</summary>
		public bool? Current { get; set; }
	}
}
