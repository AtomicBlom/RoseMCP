using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Who a debug session belongs to: a set of parties, any one of which an MCP call may be. An agent's session is the
/// MCP session that started it; a person's is the person's, and the one agent session they have shared it with.
/// <para>
/// The person is a party no MCP call can be, so a session a person starts is reached from no tool call at all until
/// they share it -- not by a stdio broker's null session, and not by an http client with no session of its own,
/// both of which an agent's session started there does admit. The person reaches every session regardless, through
/// the operator API, which asks <see cref="LiveAppSessionManager.ForOperator"/> rather than this.
/// </para>
/// <para>
/// Immutable, and replaced whole: a share swaps one set for another, so a call checking ownership on another thread
/// reads the set before the share or the set after it and never one half-changed. A set holds at most one client a
/// person shared with; sharing with another replaces it.
/// </para>
/// </summary>
public sealed class LiveAppOwners
{
	private LiveAppOwners(bool person, string? starter, string? starterKey, LiveAppSharedClient? shared)
	{
		IsPersons = person;
		Starter = starter;
		StarterKey = starterKey;
		Shared = shared;
	}

	/// <summary>A session the person started from a window, reached by no MCP call until they share it.</summary>
	public static LiveAppOwners Person { get; } = new(person: true, starter: null, starterKey: null, shared: null);

	/// <summary>
	/// A session an agent started, belonging to the MCP session <paramref name="transportSession"/> -- null where the
	/// transport has no notion of one, which every call without one then matches, as a stdio broker's has always been.
	/// <paramref name="clientKey"/> is the key the client table files that session under, which names it to a window.
	/// </summary>
	public static LiveAppOwners Agent(string? transportSession, string? clientKey = null) =>
		new(person: false, starter: transportSession, starterKey: clientKey, shared: null);

	/// <summary>A session started by the MCP call in flight, as <see cref="CallSession.Id"/> and <see cref="CallSession.ClientKey"/> name it.</summary>
	public static LiveAppOwners OfCaller() => Agent(CallSession.Id, CallSession.ClientKey);

	/// <summary>Whether the person started this session, rather than an agent.</summary>
	public bool IsPersons { get; }

	/// <summary>The MCP session an agent's session belongs to; null for a person's, and for an agent on a transport with no sessions.</summary>
	public string? Starter { get; }

	/// <summary>
	/// The key the client table files the agent session that started it under, so a window can name that session and
	/// send it a note; null for a person's. Never sent out, and never what ownership is decided by.
	/// </summary>
	public string? StarterKey { get; }

	/// <summary>The agent session the person shared this session with, or null.</summary>
	public LiveAppSharedClient? Shared { get; }

	/// <summary>Who started the session, as a summary says it.</summary>
	public LiveAppStarter StartedBy => IsPersons ? LiveAppStarter.Person : LiveAppStarter.Agent;

	/// <summary>
	/// Whether a call from <paramref name="transportSession"/>, filed by the client table under
	/// <paramref name="clientKey"/>, is one of these owners: the agent session that started it, or the client it was
	/// shared with. Never true of the person, who is no MCP call.
	/// </summary>
	public bool Admits(string? transportSession, string? clientKey)
	{
		var isStarter = !IsPersons && string.Equals(Starter, transportSession, StringComparison.Ordinal);
		if (isStarter) return true;

		return Shared is { } shared && clientKey is not null && string.Equals(shared.Key, clientKey, StringComparison.Ordinal);
	}

	/// <summary>Whether the MCP call in flight is one of these owners, by <see cref="CallSession"/>.</summary>
	public bool AdmitsCaller() => Admits(CallSession.Id, CallSession.ClientKey);

	/// <summary>These owners, with <paramref name="client"/> in place of any client the session was shared with.</summary>
	/// <exception cref="LiveAppRefusedException">An agent started the session: it is that agent's, and not the person's to give away.</exception>
	public LiveAppOwners SharedWith(LiveAppSharedClient client)
	{
		ArgumentNullException.ThrowIfNull(client);

		if (!IsPersons)
		{
			throw new LiveAppRefusedException(
				"An agent started that session, so it is that agent's: only a session you started can be shared. "
					+ "You reach it from the window as it is.");
		}

		return new LiveAppOwners(IsPersons, Starter, StarterKey, client);
	}

	/// <summary>These owners shared with nobody.</summary>
	public LiveAppOwners Unshared() => Shared is null ? this : new LiveAppOwners(IsPersons, Starter, StarterKey, shared: null);

	/// <summary>The share as a summary says it, naming the client by the row it was shared through; the key never leaves the broker.</summary>
	public LiveAppAgent? DescribeShare() => Shared is { } shared ? new LiveAppAgent { ClientId = shared.ClientId, Name = shared.Name } : null;
}

/// <summary>An agent session a person shared a debug session with.</summary>
/// <param name="Key">What the client table files the session under (<see cref="CallSession.ClientKey"/>), which its calls match. Never sent out.</param>
/// <param name="ClientId">The row it was shared through, which a window names it by.</param>
/// <param name="Name">What the row called it then: its label, folder or client name.</param>
public sealed record LiveAppSharedClient(string Key, string ClientId, string? Name);
