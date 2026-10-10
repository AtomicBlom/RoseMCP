using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RoseMcp.Contracts;
using RoseMcp.Settings;
using RoseMcp.Solutions;

namespace RoseMcp.Broker;

/// <summary>
/// What the broker remembers about each workspace's health beyond what its worker reports: when each reason
/// was first seen, which the person has accepted and what agents read about those, which reasons each agent
/// session has acknowledged, and the acceptances agents have proposed and the person has not yet decided.
/// <para>
/// Here, because each of those outlives the worker whose reasons they describe -- a reload starts a fresh
/// worker, and an eviction stops one for an hour -- and the session ones need to know which session is
/// asking, which only the broker does. The first two are the person's, per machine, and live in
/// <see cref="WorkspaceHealthFile"/>; the last two are an agent session's and die with it.
/// </para>
/// <para>
/// Everything is keyed on the solution, the kind and the project, never on a reason's wording or its id, so
/// an acceptance survives a reason being reworded and covers nothing it was not given: a new kind, or a
/// project newly failing in a kind already accepted, is not covered and still shows.
/// </para>
/// <para>
/// A session here is <see cref="CallSession.Client"/>, and a session's state goes when the session does. Nothing
/// tells the broker an http client has gone -- the transport serves each request on its own -- so a session
/// that has made no call for <see cref="SessionIdleLimit"/> is taken to have ended, which is the limit the
/// MCP transport itself holds an idle session to; <see cref="ForgetSession"/> drops one at once for a host
/// that does learn of an end. The one client of a stdio broker is the exception: its session is the
/// process's, and lasts as long as it does however long it is quiet.
/// </para>
/// </summary>
public sealed class WorkspaceHealth
{
	/// <summary>
	/// How long a session may go without a call before its acknowledgements and proposals are dropped. Two
	/// hours, which is what the MCP SDK's http transport idles a session out after, so a session this treats
	/// as ended is one the transport would have ended too.
	/// </summary>
	public static readonly TimeSpan SessionIdleLimit = TimeSpan.FromHours(2);

	/// <summary>
	/// How long a first-seen time is kept after the reason was last seen. A month, so a reason that comes and
	/// goes with a branch keeps the date it first appeared, while a removed worktree's reasons do not
	/// accumulate in the file for ever.
	/// </summary>
	public static readonly TimeSpan SeenRetention = TimeSpan.FromDays(30);

	/// <summary>
	/// How stale a last-seen time may be before seeing the reason again is written down. A day, so the tray
	/// describing every workspace every two seconds is not a write every two seconds.
	/// </summary>
	private static readonly TimeSpan SeenGranularity = TimeSpan.FromDays(1);

	/// <summary>The most proposals held at once, the oldest dropped first; a ceiling on an agent proposing in a loop.</summary>
	private const int MaxProposals = 50;

	private readonly BrokerOptions _options;
	private readonly ILogger<WorkspaceHealth> _logger;
	private readonly Lock _gate = new();

	private WorkspaceHealthRecord _record = new();
	private (DateTime WrittenUtc, long Length)? _recordStamp;

	private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
	private readonly List<Pending> _proposals = [];

	/// <summary>Whether this host serves the operator API, where a person decides proposals. See <see cref="OfferToThePerson"/>.</summary>
	private volatile bool _personCanDecide;

	/// <summary>Takes the clock, where the record is kept, and whether a call with no session is the one client, from <paramref name="options"/>.</summary>
	public WorkspaceHealth(IOptions<BrokerOptions> options, ILogger<WorkspaceHealth> logger)
	{
		_options = options.Value;
		_logger = logger;
	}

	private DateTime UtcNow => _options.TimeProvider.GetUtcNow().UtcDateTime;

	/// <summary>
	/// <paramref name="reasons"/>, from a status report or a summary of <paramref name="solution"/>, with what the
	/// broker remembers stamped on: when each was first seen, the person's acceptance where it covers every
	/// project the reason does, and when the calling session acknowledged it. Seeing a reason for the first
	/// time is recorded here, so whatever describes a workspace is what dates its reasons.
	/// </summary>
	public IReadOnlyList<HealthReason> Describe(string solution, IReadOnlyList<HealthReason> reasons)
	{
		if (reasons.Count == 0) return reasons;

		var now = UtcNow;
		var keys = reasons.SelectMany(reason => Keys(reason).Select(key => (reason.Kind, Project: key))).ToHashSet(KeyComparer.Instance);

		bool unrecorded;
		lock (_gate)
		{
			Refresh();
			unrecorded = Unrecorded(solution, keys, now);
		}

		if (unrecorded) Observe(solution, keys, now);

		lock (_gate)
		{
			var entry = Entry(_record, solution);
			var session = SessionFor(CallSession.Client, touch: false, now);
			var acknowledged = session?.Acknowledged.GetValueOrDefault(solution);

			return [.. reasons.Select(reason => reason with
			{
				FirstSeenUtc = FirstSeen(entry, reason),
				Accepted = Acceptance(entry, reason),
				AcknowledgedUtc = AcknowledgedAt(acknowledged, reason),
			})];
		}
	}

	/// <summary>
	/// What a call from the calling session to <paramref name="solution"/>'s worker sends about how it hears each
	/// kind and project: every acceptance, with what agents read about it, and the session's acknowledgements
	/// as saying nothing, which wins over an acceptance of the same kind and project. Counts as the session
	/// being alive.
	/// </summary>
	public IReadOnlyList<ReasonHearing> HeardFor(string solution)
	{
		var now = UtcNow;

		lock (_gate)
		{
			Refresh();

			var session = SessionFor(CallSession.Client, touch: true, now);
			var acknowledged = session?.Acknowledged.GetValueOrDefault(solution);
			var hearing = new Dictionary<(string Kind, string Project), ReasonHearing>(KeyComparer.Instance);

			foreach (var accepted in Entry(_record, solution)?.Accepted ?? [])
			{
				hearing[(accepted.Kind, accepted.Covering)] = new ReasonHearing
				{
					Kind = accepted.Kind,
					Covering = accepted.Covering,
					Read = ReadOf(accepted.AgentsRead),
					Note = accepted.Note,
				};
			}

			foreach (var key in acknowledged?.Keys.ToArray() ?? [])
			{
				hearing[key] = new ReasonHearing { Kind = key.Kind, Covering = key.Project, Read = AgentsRead.Nothing };
			}

			return [.. hearing.Values];
		}
	}

	/// <summary>
	/// Records that the calling session has acknowledged <paramref name="reason"/>, so its reads stop saying it
	/// until the session ends. Returns when.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// The call carries no session this broker can tell apart from its other clients'.
	/// </exception>
	public DateTime Acknowledge(string solution, HealthReason reason)
	{
		var now = UtcNow;

		lock (_gate)
		{
			var session = SessionFor(CallSession.Client, touch: true, now) ?? throw new InvalidOperationException(
				"This broker serves several clients and this call carries no session it can tell apart from theirs, so an "
					+ "acknowledgement would silence the reason for every agent using the workspace. Propose accepting it "
					+ "instead (propose: true), which waits for the person to confirm.");

			if (!session.Acknowledged.TryGetValue(solution, out var keys))
			{
				keys = new Dictionary<(string Kind, string Project), DateTime>(KeyComparer.Instance);
				session.Acknowledged[solution] = keys;
			}

			foreach (var project in Keys(reason)) keys[(reason.Kind, project)] = now;

			return now;
		}
	}

	/// <summary>
	/// A proposal from the calling session that the person accept <paramref name="reason"/>, waiting for the
	/// person. One already waiting for the same reason is the one returned, with the note replaced where a new
	/// one is given, so proposing twice leaves one thing to decide.
	/// </summary>
	public ReasonProposal Propose(string solution, HealthReason reason, string? note)
	{
		var now = UtcNow;
		if (!_personCanDecide)
		{
			throw new InvalidOperationException(
				"Nobody could confirm a proposal here: this RoseMCP host is a stdio server running on its own, with no window "
					+ "and no operator API for a person to decide it on. Start the tray, which every later session relays to, and "
					+ "propose it there; or acknowledge it for this session instead.");
		}

		var proposedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

		lock (_gate)
		{
			SessionFor(CallSession.Client, touch: true, now);

			var owner = CallSession.Client ?? string.Empty;
			var existing = _proposals.FindIndex(pending =>
				PathCasing.Comparer.Equals(pending.Proposal.Workspace, solution)
				&& string.Equals(pending.Proposal.Kind, reason.Kind, StringComparison.Ordinal)
				&& pending.Proposal.Projects.SequenceEqual(reason.Projects, StringComparer.OrdinalIgnoreCase)
				&& pending.Proposal.Subjects.SequenceEqual(reason.Subjects, StringComparer.OrdinalIgnoreCase));

			var proposal = new ReasonProposal
			{
				Id = existing >= 0 ? _proposals[existing].Proposal.Id : $"proposal-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}",
				Workspace = solution,
				WorkspaceKey = WorkspaceKey.For(solution),
				Kind = reason.Kind,
				Projects = reason.Projects,
				Subjects = reason.Subjects,
				Line = reason.Line,
				AgentsRead = proposedNote is null ? AgentsRead.RoseLine : AgentsRead.Note,
				Note = proposedNote,
				ProposedUtc = now,
			};

			if (existing >= 0) _proposals.RemoveAt(existing);

			_proposals.Add(new Pending(proposal, owner));

			if (_proposals.Count > MaxProposals) _proposals.RemoveRange(0, _proposals.Count - MaxProposals);

			return proposal;
		}
	}

	/// <summary>Every proposal waiting for the person, oldest first, from sessions that have not ended.</summary>
	public IReadOnlyList<ReasonProposal> Proposals()
	{
		lock (_gate)
		{
			ExpireSessions(UtcNow);

			return [.. _proposals.Select(pending => pending.Proposal)];
		}
	}

	/// <summary>
	/// Accepts what proposal <paramref name="id"/> proposed, with what agents read changed where the person
	/// changes it, and only then drops the proposal: a confirmation refused for want of a note, or one whose
	/// write could not land, leaves the proposal waiting to be decided again rather than gone.
	/// </summary>
	/// <exception cref="KeyNotFoundException">No proposal with that id is waiting.</exception>
	/// <exception cref="ArgumentException">Agents are to read a note and there is none.</exception>
	/// <exception cref="InvalidOperationException">The record could not be written.</exception>
	public AcceptedReasons Confirm(string id, AgentsRead? read, string? note)
	{
		ReasonProposal proposal;

		lock (_gate)
		{
			ExpireSessions(UtcNow);
			proposal = _proposals[IndexOf(id)].Proposal;
		}

		var accepted = Accept(
			proposal.Workspace,
			proposal.Kind,
			proposal.Projects,
			read ?? proposal.AgentsRead,
			note ?? proposal.Note,
			proposal.Subjects);

		lock (_gate)
		{
			_proposals.RemoveAll(pending => string.Equals(pending.Proposal.Id, id, StringComparison.Ordinal));
		}

		return accepted;
	}

	/// <summary>Drops proposal <paramref name="id"/> without accepting anything.</summary>
	/// <exception cref="KeyNotFoundException">No proposal with that id is waiting.</exception>
	public ReasonProposal Reject(string id)
	{
		lock (_gate)
		{
			ExpireSessions(UtcNow);
			return Take(id);
		}
	}

	/// <summary>
	/// Records that the person accepts <paramref name="kind"/> for each of <paramref name="projects"/> -- or, for a
	/// reason naming none, each of <paramref name="subjects"/>, as its health entry lists them -- with what agents
	/// read about it, replacing any earlier acceptance of the same kind and project or subject. Returns every acceptance the solution now has.
	/// </summary>
	/// <exception cref="ArgumentException">No kind, no projects or subjects, a solution that is not an absolute path, or a note asked for and not given.</exception>
	/// <exception cref="InvalidOperationException">The record could not be written.</exception>
	public AcceptedReasons Accept(
		string solution,
		string kind,
		IReadOnlyList<string> projects,
		AgentsRead read,
		string? note,
		IReadOnlyList<string>? subjects = null)
	{
		var path = Solution(solution);
		if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("A reason is accepted by its kind, and none was given.", nameof(kind));

		var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
		if (read == AgentsRead.Note && trimmed is null)
		{
			throw new ArgumentException("Agents are to read the person's note about this reason, and no note was given.", nameof(note));
		}

		var now = UtcNow;
		var keys = Covered(projects, subjects);

		return Change(path, entry =>
		{
			var kept = entry.Accepted.Where(accepted => !Covers(keys, kind, accepted));
			var added = keys.Select(project => new AcceptedReasonRecord
			{
				Kind = kind,
				Covering = project,
				AcceptedUtc = now,
				AgentsRead = read.ToString(),
				Note = read == AgentsRead.Note ? trimmed : null,
			});

			return entry with { Accepted = [.. kept, .. added] };
		});
	}

	/// <summary>Stops accepting <paramref name="kind"/> for each of <paramref name="projects"/>, or, for a reason naming none, each of <paramref name="subjects"/>.</summary>
	/// <exception cref="ArgumentException">No kind, no projects or subjects, or a solution that is not an absolute path.</exception>
	/// <exception cref="InvalidOperationException">The record could not be written.</exception>
	public AcceptedReasons StopAccepting(string solution, string kind, IReadOnlyList<string> projects, IReadOnlyList<string>? subjects = null)
	{
		var path = Solution(solution);
		if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("A reason is accepted by its kind, and none was given.", nameof(kind));

		var keys = Covered(projects, subjects);

		return Change(path, entry => entry with { Accepted = [.. entry.Accepted.Where(accepted => !Covers(keys, kind, accepted))] });
	}

	/// <summary>Every acceptance <paramref name="solution"/> has.</summary>
	/// <exception cref="ArgumentException">A solution that is not an absolute path.</exception>
	public AcceptedReasons Accepted(string solution)
	{
		var path = Solution(solution);

		lock (_gate)
		{
			Refresh();
			return AcceptedOf(path, Entry(_record, path));
		}
	}

	/// <summary>Drops everything held for session <paramref name="sessionId"/>: its acknowledgements and the proposals it made.</summary>
	public void ForgetSession(string? sessionId)
	{
		var key = sessionId ?? string.Empty;

		lock (_gate)
		{
			_sessions.Remove(key);
			_proposals.RemoveAll(pending => string.Equals(pending.Owner, key, StringComparison.Ordinal));
		}
	}

	/// <summary>
	/// Writes a change to one solution's entry, creating it, and answers with the solution's acceptances after.
	/// The file is written outside the gate, so a write waiting on another process never holds up a tool call
	/// asking what it hears.
	/// </summary>
	/// <exception cref="InvalidOperationException">The record could not be written; the message says why.</exception>
	private AcceptedReasons Change(string solution, Func<SolutionHealthRecord, SolutionHealthRecord> change)
	{
		var write = WorkspaceHealthFile.Update(record => WithEntry(record, solution, change), _options.LocalAppData, _options.HealthRecordWait);

		if (write.Record is not { } written)
		{
			throw new InvalidOperationException($"Nothing was changed, because the record of accepted reasons could not be written: {write.Failure}");
		}

		lock (_gate)
		{
			Remember(written);
		}

		return AcceptedOf(solution, Entry(written, solution));
	}

	/// <summary>Whether any of <paramref name="keys"/> has never been seen, or was last seen too long ago to leave unrecorded. Under the gate.</summary>
	private bool Unrecorded(string solution, IReadOnlySet<(string Kind, string Project)> keys, DateTime now)
	{
		var seen = Entry(_record, solution)?.Seen ?? [];

		return keys.Any(key => seen.FirstOrDefault(entry => Matches(entry.Kind, entry.Covering, key)) is not { } entry
			|| now - entry.LastSeenUtc > SeenGranularity);
	}

	/// <summary>
	/// Records first and last sightings of every kind and project <paramref name="keys"/> name, and drops sightings
	/// past <see cref="SeenRetention"/> that nothing accepts. Not under the gate: the file write can wait on another
	/// process, and every tool call takes the gate to ask what it hears. Where the write cannot land the sighting is
	/// kept in memory, so a dated reason stays dated and the next description does not wait on the file again.
	/// </summary>
	private void Observe(string solution, IReadOnlySet<(string Kind, string Project)> keys, DateTime now)
	{
		var write = WorkspaceHealthFile.Update(record => Observed(record, solution, keys, now), _options.LocalAppData, _options.HealthRecordWait);

		lock (_gate)
		{
			if (write.Record is { } observed)
			{
				Remember(observed);
				return;
			}

			_logger.LogDebug("Could not record when the reasons for {Solution} were seen, so it is kept in memory: {Failure}", solution, write.Failure);
			_record = Observed(_record, solution, keys, now);
		}
	}

	/// <summary><paramref name="record"/> with every key in <paramref name="keys"/> seen now, and old unaccepted sightings pruned.</summary>
	private static WorkspaceHealthRecord Observed(
		WorkspaceHealthRecord record,
		string solution,
		IReadOnlySet<(string Kind, string Project)> keys,
		DateTime now) =>
		WithEntry(record, solution, entry =>
		{
			var seen = new List<SeenReasonRecord>();

			foreach (var sighting in entry.Seen)
			{
				var key = (sighting.Kind, sighting.Covering);
				var current = keys.Contains(key);
				var accepted = entry.Accepted.Any(acceptance => Matches(acceptance.Kind, acceptance.Covering, key));
				var forgotten = !current && !accepted && now - sighting.LastSeenUtc > SeenRetention;

				if (forgotten) continue;

				seen.Add(current && now - sighting.LastSeenUtc > SeenGranularity ? sighting with { LastSeenUtc = now } : sighting);
			}

			foreach (var key in keys.Where(key => !seen.Any(sighting => Matches(sighting.Kind, sighting.Covering, key))))
			{
				seen.Add(new SeenReasonRecord { Kind = key.Kind, Covering = key.Project, FirstSeenUtc = now, LastSeenUtc = now });
			}

			return entry with { Seen = seen };
		});

	/// <summary><paramref name="record"/> with <paramref name="solution"/>'s entry changed, made where there was none and dropped where it is left empty.</summary>
	private static WorkspaceHealthRecord WithEntry(
		WorkspaceHealthRecord record,
		string solution,
		Func<SolutionHealthRecord, SolutionHealthRecord> change)
	{
		var entry = Entry(record, solution) ?? new SolutionHealthRecord { Solution = solution };
		var changed = change(entry);
		var others = record.Solutions.Where(other => !PathCasing.Comparer.Equals(other.Solution, solution));
		var empty = changed.Accepted.Count == 0 && changed.Seen.Count == 0;

		return record with { Solutions = empty ? [.. others] : [.. others, changed] };
	}

	/// <summary>Re-reads the record where the file has changed since it was last read, by this process or another. Under the gate.</summary>
	private void Refresh()
	{
		var path = WorkspaceHealthFile.PathFor(_options.LocalAppData);
		var stamp = Stamp(path);

		if (stamp == _recordStamp) return;

		// A file another process holds this moment is read again next time, rather than taken to hold nothing.
		if (!WorkspaceHealthFile.TryRead(_options.LocalAppData, out var record)) return;

		_record = record;
		_recordStamp = stamp;
	}

	/// <summary>
	/// Holds what was just written as the record until the next refresh reads the file again, which it does: the
	/// write landed outside the gate, so another process may have written after it, and stamping the file now would
	/// take that write for this one. Under the gate.
	/// </summary>
	private void Remember(WorkspaceHealthRecord written)
	{
		_record = written;
		_recordStamp = null;
	}

	private static (DateTime WrittenUtc, long Length)? Stamp(string path)
	{
		try
		{
			var file = new FileInfo(path);
			return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// The calling session's state, made where there is none, or null where the call has no session this broker
	/// can tell apart from its other clients'. Ends sessions idle past <see cref="SessionIdleLimit"/> first. Under the gate.
	/// </summary>
	private SessionState? SessionFor(string? sessionId, bool touch, DateTime now)
	{
		ExpireSessions(now);

		if (sessionId is null && !_options.ServesOneClient) return null;

		var key = sessionId ?? string.Empty;

		if (!_sessions.TryGetValue(key, out var session))
		{
			if (!touch) return null;

			session = new SessionState();
			_sessions[key] = session;
		}

		if (touch) session.LastCallUtc = now;

		return session;
	}

	/// <summary>Drops every session idle past the limit, with the proposals it made. Under the gate.</summary>
	private void ExpireSessions(DateTime now)
	{
		// The one client of a stdio broker is never taken to have gone: its session is the process's, and ends with it.
		var oneClient = _options.ServesOneClient;
		var ended = _sessions
			.Where(pair => !(oneClient && pair.Key.Length == 0) && now - pair.Value.LastCallUtc > SessionIdleLimit)
			.Select(pair => pair.Key)
			.ToHashSet(StringComparer.Ordinal);

		var idleOwners = _proposals
			.Select(pending => pending.Owner)
			.Where(owner => !_sessions.ContainsKey(owner))
			.ToHashSet(StringComparer.Ordinal);

		foreach (var key in ended) _sessions.Remove(key);

		// A proposal outlives its session's acknowledgements by nothing: the person deciding it later is deciding
		// for a session that has gone. One from a session that never acknowledged anything is aged by its own time.
		_proposals.RemoveAll(pending => ended.Contains(pending.Owner)
			|| (idleOwners.Contains(pending.Owner) && now - pending.Proposal.ProposedUtc > SessionIdleLimit));
	}

	/// <summary>Takes proposal <paramref name="id"/> out of the waiting list. Under the gate.</summary>
	private ReasonProposal Take(string id)
	{
		var index = IndexOf(id);
		var proposal = _proposals[index].Proposal;
		_proposals.RemoveAt(index);

		return proposal;
	}

	/// <summary>Where proposal <paramref name="id"/> is in the waiting list. Under the gate.</summary>
	/// <exception cref="KeyNotFoundException">No proposal with that id is waiting.</exception>
	private int IndexOf(string id)
	{
		var index = _proposals.FindIndex(pending => string.Equals(pending.Proposal.Id, id, StringComparison.Ordinal));
		if (index >= 0) return index;

		throw new KeyNotFoundException(
			$"No proposal '{id}' is waiting. GET {OperatorApi.Prefix}/health/proposals lists the ones there are; a proposal "
				+ "goes when it is decided, or when the session that made it ends.");
	}

	/// <summary>
	/// Says a person can decide proposals in this host: it serves the operator API, behind its token. Called by
	/// <see cref="OperatorApi.MapRoseOperatorApi"/>, so a host that maps it cannot forget to say so, and a stdio
	/// server running alone -- which maps nothing a person could reach -- refuses proposals nobody could confirm.
	/// </summary>
	public void OfferToThePerson() => _personCanDecide = true;

	private static SolutionHealthRecord? Entry(WorkspaceHealthRecord record, string solution) =>
		record.Solutions.FirstOrDefault(entry => PathCasing.Comparer.Equals(entry.Solution, solution));

	private static AcceptedReasons AcceptedOf(string solution, SolutionHealthRecord? entry) => new()
	{
		Workspace = solution,
		WorkspaceKey = WorkspaceKey.For(solution),
		Accepted = [.. (entry?.Accepted ?? []).Select(accepted => new AcceptedReason
		{
			Kind = accepted.Kind,
			Covering = accepted.Covering,
			AcceptedUtc = accepted.AcceptedUtc,
			AgentsRead = ReadOf(accepted.AgentsRead),
			Note = accepted.Note,
		})],
	};

	/// <summary>The earliest first sighting of any project the reason covers, or null where none is recorded.</summary>
	private static DateTime? FirstSeen(SolutionHealthRecord? entry, HealthReason reason)
	{
		var sightings = Keys(reason)
			.Select(project => entry?.Seen.FirstOrDefault(seen => Matches(seen.Kind, seen.Covering, (reason.Kind, project))))
			.OfType<SeenReasonRecord>()
			.ToArray();

		return sightings.Length == 0 ? null : sightings.Min(seen => seen.FirstSeenUtc);
	}

	/// <summary>
	/// The person's acceptance of <paramref name="reason"/>, where every project it covers has one for its kind;
	/// null otherwise. Several are read as the most an agent is owed: Rose's line over a note, a note over nothing.
	/// </summary>
	private static ReasonAcceptance? Acceptance(SolutionHealthRecord? entry, HealthReason reason)
	{
		var covering = new List<AcceptedReasonRecord>();

		foreach (var project in Keys(reason))
		{
			if (entry?.Accepted.FirstOrDefault(accepted => Matches(accepted.Kind, accepted.Covering, (reason.Kind, project))) is not { } match)
			{
				return null;
			}

			covering.Add(match);
		}

		var chosen = covering.FirstOrDefault(accepted => ReadOf(accepted.AgentsRead) == AgentsRead.RoseLine)
			?? covering.FirstOrDefault(accepted => ReadOf(accepted.AgentsRead) == AgentsRead.Note)
			?? covering[0];

		return new ReasonAcceptance
		{
			AcceptedUtc = covering.Min(accepted => accepted.AcceptedUtc),
			AgentsRead = ReadOf(chosen.AgentsRead),
			Note = chosen.Note,
		};
	}

	/// <summary>When the session acknowledged every project <paramref name="reason"/> covers, the latest of them; null where it has not.</summary>
	private static DateTime? AcknowledgedAt(Dictionary<(string Kind, string Project), DateTime>? acknowledged, HealthReason reason)
	{
		if (acknowledged is null) return null;

		var times = new List<DateTime>();

		foreach (var project in Keys(reason))
		{
			if (!acknowledged.TryGetValue((reason.Kind, project), out var at)) return null;

			times.Add(at);
		}

		return times.Max();
	}

	/// <summary>A stored choice read back, Rose's line where it names nothing this build knows: the choice that hides nothing.</summary>
	private static AgentsRead ReadOf(string stored) =>
		Enum.TryParse<AgentsRead>(stored, ignoreCase: true, out var read) && Enum.IsDefined(read) ? read : AgentsRead.RoseLine;

	/// <summary>What an acceptance of <paramref name="reason"/> keys on: each project it names, or each subject.</summary>
	private static IReadOnlyList<string> Keys(HealthReason reason) => KeysOf(reason.Projects, reason.Subjects);

	/// <summary>
	/// What a person's acceptance covers: the projects named, or the subjects where none is. Refused where neither
	/// is named, since every reason in health names one or the other, and an acceptance of neither would match no
	/// reason while the person was told it had worked.
	/// </summary>
	/// <exception cref="ArgumentException">Neither projects nor subjects were named.</exception>
	private static IReadOnlyList<string> Covered(IReadOnlyList<string> projects, IReadOnlyList<string>? subjects)
	{
		var keys = KeysOf(projects, subjects);
		if (keys is not [""]) return keys;

		throw new ArgumentException(
			"Name what the acceptance covers: the projects the reason's health entry lists, or, for a reason that names "
				+ "no project, the subjects it lists instead.",
			nameof(projects));
	}

	/// <summary>
	/// What an acceptance keys on: each project named, or, naming none, each subject, or the whole solution
	/// where there is neither. A kind covers either projects or subjects, never both, so the two never meet in
	/// one key.
	/// </summary>
	private static IReadOnlyList<string> KeysOf(IReadOnlyList<string> projects, IReadOnlyList<string>? subjects)
	{
		var named = Named(projects);
		if (named.Length > 0) return named;

		var about = Named(subjects ?? []);

		return about.Length == 0 ? [string.Empty] : about;

		static string[] Named(IReadOnlyList<string> items) =>
			[.. items.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	private static bool Covers(IReadOnlyList<string> projects, string kind, AcceptedReasonRecord accepted) =>
		string.Equals(accepted.Kind, kind, StringComparison.Ordinal)
		&& projects.Contains(accepted.Covering, StringComparer.OrdinalIgnoreCase);

	private static bool Matches(string kind, string project, (string Kind, string Project) key) =>
		string.Equals(kind, key.Kind, StringComparison.Ordinal)
		&& string.Equals(project, key.Project, StringComparison.OrdinalIgnoreCase);

	/// <summary>A solution as the record names it: absolute, refused otherwise, since a relative one names nothing a later call can match.</summary>
	private static string Solution(string solution)
	{
		if (string.IsNullOrWhiteSpace(solution) || !Path.IsPathFullyQualified(solution))
		{
			throw new ArgumentException(
				$"A solution is named by its absolute path, as a workspace summary gives it, and '{solution}' is not one.",
				nameof(solution));
		}

		return Path.GetFullPath(solution);
	}

	/// <summary>One session's acknowledgements, per solution, and when it last called.</summary>
	private sealed class SessionState
	{
		public DateTime LastCallUtc { get; set; }

		public Dictionary<string, Dictionary<(string Kind, string Project), DateTime>> Acknowledged { get; } = new(PathCasing.Comparer);
	}

	/// <summary>A proposal and the session that made it, the empty string for the one client of a stdio broker.</summary>
	private sealed record Pending(ReasonProposal Proposal, string Owner);

	/// <summary>Kinds compared exactly, project names ignoring case, as a project name is everywhere else.</summary>
	private sealed class KeyComparer : IEqualityComparer<(string Kind, string Project)>
	{
		public static readonly KeyComparer Instance = new();

		public bool Equals((string Kind, string Project) one, (string Kind, string Project) other) =>
			string.Equals(one.Kind, other.Kind, StringComparison.Ordinal)
			&& string.Equals(one.Project, other.Project, StringComparison.OrdinalIgnoreCase);

		public int GetHashCode((string Kind, string Project) key) =>
			HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.Kind), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Project));
	}
}
