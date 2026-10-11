using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The broker's table of clients: who each one is, when it connected, when it last called, and whether it
/// holds the tools this broker offers. What a window reads to say which agent sessions are using Rose.
/// <para>
/// A relayed client is the session its relay names in <c>_meta["rosemcp/session"]</c>, which the relay mints
/// once for the life of its process. So two sessions in one folder are two relay processes and two clients,
/// and the subagents of one session, which share its one MCP connection and so its one relay, stay one. What
/// the relay says of its client arrives at <see cref="RelayProtocol.ClientRoute"/> and is joined to the calls
/// by that name. A client that reached the broker over http without a relay is unnamed: it is told apart by
/// its transport session where the transport gives one, and where it gives none -- the current protocol
/// revision serves each request on its own -- every such client shares one unnamed row, since nothing in
/// the request says which of them it is.
/// </para>
/// <para>
/// The broker outlives every client, so a row goes when its client does. A relay is connected while it holds
/// <see cref="RelayProtocol.WatchRoute"/> open; its row stays while it does, and goes once <see cref="GoneGrace"/>
/// has passed since the later of the client's last word and the moment its relay let go of the watch, which
/// covers a relay re-opening its watch however long its client had been quiet before. Nothing tells the broker
/// an unnamed client has gone, so its row goes once it has been quiet for <see cref="IdleLimit"/>, the limit
/// the MCP transport holds an idle session to; so does a named row whose relay never said whom it serves,
/// which has no watch to go by. <see cref="Capacity"/> bounds the whole, since every row is made at a
/// client's say-so.
/// </para>
/// <para>
/// One gate, held only to read and change the rows: no I/O happens under it, and the relays watching are
/// read before it is taken, so it never nests inside <see cref="RelayConnections"/>'s or holds that one up.
/// Recording a call never throws, because it runs on every tool call and a table of who called is not a
/// reason for a call to fail.
/// </para>
/// </summary>
public sealed class ConnectedClients
{
	/// <summary>The most clients kept at once. A newcomer past it takes the place of the quietest client whose relay is not watching, or goes unrecorded.</summary>
	public const int Capacity = 256;

	/// <summary>How long a client with no watch to go by may be quiet before its row goes: the MCP transport's own idle limit.</summary>
	public static readonly TimeSpan IdleLimit = WorkspaceHealth.SessionIdleLimit;

	/// <summary>How long a relayed client whose relay has stopped watching may be quiet before its row goes.</summary>
	public static readonly TimeSpan GoneGrace = TimeSpan.FromMinutes(2);

	private const int MaxRoots = 16;
	private const int MaxPath = 1024;
	private const int MaxBranch = 256;
	private const int MaxName = 128;
	private const int MaxVersion = 64;
	private const int MaxLabel = 200;

	private readonly RelayConnections _relays;
	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);

	/// <summary>Reads which relays are watching from <paramref name="relays"/>, and the clock from <paramref name="options"/>.</summary>
	public ConnectedClients(RelayConnections relays, IOptions<BrokerOptions> options)
	{
		_relays = relays;
		_time = options.Value.TimeProvider;
	}

	private DateTimeOffset Now => _time.GetUtcNow();

	/// <summary>
	/// Records a tool call, making the client's row where it has none.
	/// </summary>
	/// <param name="relayed">The client a relay named, as <see cref="CallSession.Of"/> gives it with no transport session; null for none.</param>
	/// <param name="transportSession">The transport's own session, where it has one.</param>
	/// <param name="clientName">The name in the client's <c>clientInfo</c>, as the transport has it; kept only for an unnamed client.</param>
	/// <param name="clientVersion">The version in the same.</param>
	public void Called(string? relayed, string? transportSession, string? clientName, string? clientVersion) =>
		Touch(relayed, transportSession, clientName, clientVersion, Contact.Call);

	/// <summary>
	/// Records a tool listing. It makes a row only for an unnamed client, which lists before it calls: a relay
	/// lists the broker for itself whenever it connects, before its client has asked for anything.
	/// </summary>
	public void Listed(string? relayed, string? transportSession, string? clientName, string? clientVersion) =>
		Touch(relayed, transportSession, clientName, clientVersion, Contact.Listing);

	/// <summary>
	/// Records a read a relay made for itself under its session's name -- which debug sessions are open, after a
	/// call that may have changed them. It says the client is still there and is not one of the client's calls,
	/// so it makes no row and counts nothing.
	/// </summary>
	public void ReadByRelay(string? relayed, string? transportSession) =>
		Touch(relayed, transportSession, clientName: null, clientVersion: null, Contact.RelayRead);

	/// <summary>Takes what relay <paramref name="relayId"/> says of the client it serves, replacing what it said before.</summary>
	/// <exception cref="ArgumentException">The relay id, or the session it names, is not in the shape a relay mints.</exception>
	/// <exception cref="InvalidOperationException">
	/// The session is already another relay's, or the table is full of clients whose relays are watching.
	/// </exception>
	public void Introduce(string relayId, RelayClient client)
	{
		if (!RelayProtocol.IsRelayId(relayId))
		{
			throw new ArgumentException(
				$"A relay names itself in the {RelayProtocol.RelayHeader} header with thirty-two lowercase hex digits.",
				nameof(relayId));
		}

		var relayed = CallSession.Relayed(client.Session)
			?? throw new ArgumentException(
				"A relay names the session it serves with the id it sends as _meta[\"rosemcp/session\"]: sixteen to sixty-four lowercase hex digits.",
				nameof(client));

		var facts = Sanitised(client);
		var watchers = _relays.Watchers();
		var now = Now;

		lock (_gate)
		{
			Expire(watchers, now);

			var key = KeyOf(relayed, transportSession: null);
			if (_rows.TryGetValue(key, out var row))
			{
				var anotherRelays = row.RelayId is { } bound && !string.Equals(bound, relayId, StringComparison.Ordinal);
				if (anotherRelays) throw new InvalidOperationException("That session is served by another relay, which said so first.");
			}
			else
			{
				if (!MakeRoom(watchers))
				{
					throw new InvalidOperationException(
						$"The broker already keeps {Capacity} clients, every one with a relay watching, so this one is not recorded.");
				}

				row = new Row(MintId(), named: true, now);
				_rows[key] = row;
			}

			row.RelayId = relayId;
			row.Facts = facts;
			row.LastSeenUtc = now;
		}
	}

	/// <summary>Every client the broker is keeping, the earliest connected first.</summary>
	public IReadOnlyList<ClientSummary> Describe()
	{
		var watchers = _relays.Watchers();
		var now = Now;

		lock (_gate)
		{
			Expire(watchers, now);

			return
			[
				.. _rows.Values
					.OrderBy(row => row.ConnectedUtc)
					.ThenBy(row => row.Id, StringComparer.Ordinal)
					.Select(row => row.Describe(watchers)),
			];
		}
	}

	/// <summary>
	/// <paramref name="client"/> as the broker keeps it: each string without control or formatting characters
	/// and cut to a length, at most <see cref="MaxRoots"/> roots, a process id only where it is positive, and a
	/// tool hash only in the shape one has. What a relay sends is shown to a person and written to the log.
	/// </summary>
	public static RelayClient Sanitised(RelayClient client) => new()
	{
		Session = client.Session,
		WorkingDirectory = Clean(client.WorkingDirectory, MaxPath),
		Roots = [.. (client.Roots ?? []).Select(root => Clean(root, MaxPath)).OfType<string>().Take(MaxRoots)],
		Branch = Clean(client.Branch, MaxBranch),
		ClientProcessId = client.ClientProcessId is > 0 ? client.ClientProcessId : null,
		ClientProcessName = Clean(client.ClientProcessName, MaxName),
		ClientName = Clean(client.ClientName, MaxName),
		ClientVersion = Clean(client.ClientVersion, MaxVersion),
		Label = Clean(client.Label, MaxLabel),
		HeldTools = IsToolHash(client.HeldTools) ? client.HeldTools : null,
		ToolsCurrent = client.ToolsCurrent,
	};

	/// <summary>
	/// <paramref name="value"/> without control or formatting characters -- a line break, an escape sequence, a
	/// right-to-left override -- trimmed, and cut to <paramref name="max"/> characters; null where nothing is left.
	/// </summary>
	public static string? Clean(string? value, int max)
	{
		if (value is null) return null;

		var kept = new StringBuilder(Math.Min(value.Length, max));
		foreach (var character in value)
		{
			var category = char.GetUnicodeCategory(character);
			var unprintable = char.IsControl(character) || category is UnicodeCategory.Format or UnicodeCategory.Surrogate;
			if (unprintable) continue;

			kept.Append(character);
			if (kept.Length == max) break;
		}

		var cleaned = kept.ToString().Trim();

		return cleaned.Length == 0 ? null : cleaned;
	}

	private static bool IsToolHash(string? value) =>
		value is { Length: 64 } && value.All(character => char.IsAsciiDigit(character) || char.IsAsciiHexDigitLower(character));

	private static string KeyOf(string? relayed, string? transportSession) =>
		relayed is not null ? $"relay:{relayed}"
			: transportSession is not null ? $"transport:{transportSession}"
			: "unnamed";

	private static string MintId() => "client-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

	private void Touch(string? relayed, string? transportSession, string? clientName, string? clientVersion, Contact contact)
	{
		var named = relayed is not null;
		var transport = string.IsNullOrWhiteSpace(transportSession) ? null : transportSession;
		var watchers = _relays.Watchers();
		var now = Now;

		lock (_gate)
		{
			Expire(watchers, now);

			var key = KeyOf(relayed, transport);
			if (!_rows.TryGetValue(key, out var row))
			{
				var makesARow = contact == Contact.Call || (contact == Contact.Listing && !named);
				if (!makesARow || !MakeRoom(watchers)) return;

				row = new Row(MintId(), named, now);
				_rows[key] = row;
			}

			row.LastSeenUtc = now;

			if (contact == Contact.Call)
			{
				row.LastCallUtc = now;
				row.Calls++;
			}
			else if (contact == Contact.Listing)
			{
				row.Listed = true;
			}

			if (!named)
			{
				row.ClientName = Clean(clientName, MaxName) ?? row.ClientName;
				row.ClientVersion = Clean(clientVersion, MaxVersion) ?? row.ClientVersion;
			}
		}
	}

	/// <summary>Drops every row whose client has gone, by the rules in the class summary. Under the gate.</summary>
	private void Expire(RelayWatchers watchers, DateTimeOffset now)
	{
		var gone = _rows
			.Where(pair => pair.Value.HasGone(watchers, now))
			.Select(pair => pair.Key)
			.ToList();

		foreach (var key in gone) _rows.Remove(key);
	}

	/// <summary>
	/// Whether there is room for one more row, making it by dropping the quietest row whose relay is not
	/// watching where the table is full. Under the gate.
	/// </summary>
	private bool MakeRoom(RelayWatchers watchers)
	{
		if (_rows.Count < Capacity) return true;

		var quietest = _rows
			.Where(pair => !pair.Value.IsWatching(watchers))
			.OrderBy(pair => pair.Value.LastSeenUtc)
			.Select(pair => pair.Key)
			.FirstOrDefault();

		if (quietest is null) return false;

		_rows.Remove(quietest);
		return true;
	}

	/// <summary>What brought a client to the table's notice.</summary>
	private enum Contact
	{
		/// <summary>A tool call of the client's own: counted, and it makes the client's row.</summary>
		Call,

		/// <summary>A tool listing: it makes a row only for an unnamed client.</summary>
		Listing,

		/// <summary>A read its relay made for itself: it says the client is there, and nothing else.</summary>
		RelayRead,
	}

	/// <summary>One client. Read and written only under the table's gate.</summary>
	private sealed class Row(string id, bool named, DateTimeOffset connectedUtc)
	{
		public string Id { get; } = id;

		public bool Named { get; } = named;

		public DateTimeOffset ConnectedUtc { get; } = connectedUtc;

		/// <summary>When the client last called, listed or, through its relay, said whom it is.</summary>
		public DateTimeOffset LastSeenUtc { get; set; } = connectedUtc;

		public DateTimeOffset? LastCallUtc { get; set; }

		public int Calls { get; set; }

		/// <summary>Whether the client has listed the tools from this broker.</summary>
		public bool Listed { get; set; }

		/// <summary>The relay that said whom this client is, once one has.</summary>
		public string? RelayId { get; set; }

		/// <summary>What the relay said, sanitised.</summary>
		public RelayClient? Facts { get; set; }

		/// <summary>An unnamed client's <c>clientInfo</c>, as its transport gave it.</summary>
		public string? ClientName { get; set; }

		public string? ClientVersion { get; set; }

		public bool IsWatching(RelayWatchers watchers) => RelayId is { } relay && watchers.IsWatching(relay);

		/// <summary>
		/// Whether the client has gone: never while its relay watches; otherwise once it has been quiet past the
		/// limit, counted from the later of its last word and its relay letting go, so a client quiet for an hour
		/// whose relay is re-opening its watch is not taken for gone in the moment between.
		/// </summary>
		public bool HasGone(RelayWatchers watchers, DateTimeOffset now)
		{
			if (IsWatching(watchers)) return false;

			var stopped = RelayId is { } relay ? watchers.StoppedUtc(relay) : null;
			var since = stopped is { } ended && ended > LastSeenUtc ? ended : LastSeenUtc;
			var limit = RelayId is null ? IdleLimit : GoneGrace;

			return now - since > limit;
		}

		public ClientSummary Describe(RelayWatchers watchers) => new()
		{
			Id = Id,
			Named = Named,
			ConnectedUtc = ConnectedUtc,
			LastCallUtc = LastCallUtc,
			Calls = Calls,
			Watching = Named ? IsWatching(watchers) : null,
			RelayProcessId = RelayId is { } relay && watchers.Watching.TryGetValue(relay, out var process) ? process : null,
			ToolsCurrent = Named ? Facts?.ToolsCurrent : UnnamedToolsCurrent(),
			HeldTools = Facts?.HeldTools,
			WorkingDirectory = Facts?.WorkingDirectory,
			Roots = Facts?.Roots ?? [],
			Branch = Facts?.Branch,
			ClientProcessId = Facts?.ClientProcessId,
			ClientProcessName = Facts?.ClientProcessName,
			ClientName = Facts?.ClientName ?? ClientName,
			ClientVersion = Facts?.ClientVersion ?? ClientVersion,
			Label = Facts?.Label,
		};

		/// <summary>
		/// A listing from this broker is its current tools; calling without one means the list came from a
		/// broker before this one. Nothing either way says nothing.
		/// </summary>
		private bool? UnnamedToolsCurrent() => Listed ? true : Calls > 0 ? false : null;
	}
}
