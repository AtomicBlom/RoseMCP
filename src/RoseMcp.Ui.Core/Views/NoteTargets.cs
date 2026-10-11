using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// Which agent sessions a note about something can go to, from the client table and the activity record: for a call,
/// the session that made it; for a workspace's reason, every session the broker can tell apart, those that used the
/// workspace first.
/// <para>
/// A session is found by its row first and then by the key its calls carry: a relay outlives a broker restart and
/// keeps its session's key, where the row it gets from the new broker is new, so a call made before the restart
/// still reaches the session that made it. A client without the relay is never offered, since its row may stand for
/// any number of clients and the broker refuses a note to it.
/// </para>
/// </summary>
public static class NoteTargets
{
	/// <summary>Why a call's composer cannot send, where the call came from a client without the relay.</summary>
	public const string UnnamedCaller =
		"This call came from a client without the relay, which Rose cannot tell apart from others, so a note cannot reach it.";

	/// <summary>Why a call's composer cannot send, where the session that made it has gone.</summary>
	public const string CallerGone = "The session that made this call is no longer connected, so a note cannot reach it.";

	/// <summary>Why a reason's composer cannot send: no session the broker can tell apart is connected.</summary>
	public const string NobodyToAsk = "No agent session is connected through the relay, so there is nobody to ask.";

	/// <summary>Offers each call among <paramref name="rows"/> the session that made it, or says why there is none.</summary>
	public static void Address(IEnumerable<OperationRow> rows, IReadOnlyList<ClientSummary> clients, IReadOnlyList<ActivityEntry> entries)
	{
		var current = CurrentRows(clients, entries);

		foreach (var row in rows.Where(row => row.IsCall))
		{
			var recipients = CallerOf(row.ClientId, row.ClientKey, clients, current);
			var unnamed = clients.Any(client => client.Id == row.ClientId && !client.Named);

			row.Composer.Offer(recipients, recipients.Count > 0 ? string.Empty : unnamed ? UnnamedCaller : CallerGone);
		}
	}

	/// <summary>The session that made a call, as the table has it now: one recipient, or none where it has gone or nothing names it.</summary>
	public static IReadOnlyList<NoteRecipient> CallerOf(
		string? clientId,
		string? clientKey,
		IReadOnlyList<ClientSummary> clients,
		IReadOnlyList<ActivityEntry> entries) =>
		CallerOf(clientId, clientKey, clients, CurrentRows(clients, entries));

	/// <summary>
	/// Every session a note about something in <paramref name="solution"/> can go to: those whose calls the record holds
	/// for it, the newest caller first, then every other session the broker can tell apart.
	/// </summary>
	public static IReadOnlyList<NoteRecipient> For(string solution, IReadOnlyList<ClientSummary> clients, IReadOnlyList<ActivityEntry> entries)
	{
		var current = CurrentRows(clients, entries);
		var named = clients.Where(client => client.Named).ToList();

		var users = entries
			.Where(entry => entry.Kind == ActivityEntryKind.Call && entry.Workspace is { } path && PathCasing.Comparer.Equals(path, solution))
			.OrderByDescending(entry => entry.StartedUtc)
			.Select(entry => RowOf(entry.ClientId, entry.ClientKey, named, current))
			.OfType<ClientSummary>()
			.DistinctBy(client => client.Id)
			.ToList();

		var others = AgentCard.Order(named.Where(client => users.All(user => user.Id != client.Id)));

		return [.. users.Concat(others).Select(client => Recipient(client, named))];
	}

	/// <summary>What a session is called in a composer's list: its name, with its row where two sessions share one.</summary>
	public static NoteRecipient Recipient(ClientSummary client, IReadOnlyList<ClientSummary> clients)
	{
		var name = AgentNames.Of(client);
		var shared = clients.Count(other => other.Named && AgentNames.Of(other) == name) > 1;

		return new NoteRecipient(client.Id, shared ? $"{name} ({client.Id})" : name);
	}

	private static IReadOnlyList<NoteRecipient> CallerOf(
		string? clientId,
		string? clientKey,
		IReadOnlyList<ClientSummary> clients,
		IReadOnlyDictionary<string, string> current)
	{
		var named = clients.Where(client => client.Named).ToList();

		return RowOf(clientId, clientKey, named, current) is { } row ? [Recipient(row, named)] : [];
	}

	/// <summary>The named row a call's client has now: its own row where the table still has it, else the row its key's calls name.</summary>
	private static ClientSummary? RowOf(
		string? clientId,
		string? clientKey,
		IReadOnlyList<ClientSummary> named,
		IReadOnlyDictionary<string, string> current)
	{
		var own = named.FirstOrDefault(client => client.Id == clientId);
		if (own is not null) return own;

		return clientKey is { Length: > 0 } && current.TryGetValue(clientKey, out var id)
			? named.FirstOrDefault(client => client.Id == id)
			: null;
	}

	/// <summary>Each key the record's calls carry, to the row in the table now that a call with that key named.</summary>
	private static Dictionary<string, string> CurrentRows(IReadOnlyList<ClientSummary> clients, IReadOnlyList<ActivityEntry> entries)
	{
		var rows = clients.Where(client => client.Named).Select(client => client.Id).ToHashSet(StringComparer.Ordinal);
		var current = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var entry in entries.OrderByDescending(entry => entry.StartedUtc))
		{
			var named = entry.ClientKey is { Length: > 0 } key && entry.ClientId is { } id && rows.Contains(id);
			if (named) current.TryAdd(entry.ClientKey!, entry.ClientId!);
		}

		return current;
	}
}
