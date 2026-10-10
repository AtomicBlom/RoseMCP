using System.Text.Json;

using ModelContextProtocol.Protocol;

using RoseMcp.Contracts;

namespace RoseMcp.Server;

/// <summary>
/// The debug sessions this session has open, as of the last call that started or ended one, so a
/// restart notice can name the ones that ended with the broker.
/// <para>
/// They have to be read while the broker is alive, because the sessions die with it and the new
/// broker never heard of them. The relay asks <c>rose_debug_list</c>, which answers with the
/// sessions the calling connection started -- and every call this session makes goes through the
/// relay's one connection, so the answer is exactly this session's. It asks only after the calls
/// that change the answer, rather than on a timer, so a session that never debugs costs nothing.
/// </para>
/// <para>
/// Readings are numbered when they start, and one that finishes after a later one is dropped, so
/// concurrent calls cannot leave the older list standing. Taking the list forgets it and every
/// reading still in flight, since those are of the broker that has gone.
/// </para>
/// </summary>
public sealed class DebugSessionMemory
{
	private readonly Lock _gate = new();
	private IReadOnlyList<DebugSessionSeen> _open = [];
	private long _started;
	private long _newest;

	/// <summary>Whether a call to <paramref name="tool"/> can change which debug sessions this session has.</summary>
	public static bool Changes(string tool) =>
		tool is ToolNames.DebugAttach or ToolNames.DebugLaunch or ToolNames.DebugLaunchUwp or ToolNames.DebugDetach;

	/// <summary>
	/// The sessions a <c>rose_debug_list</c> result names that are still alive. One the broker
	/// already lists as ended -- its host died -- did not end with the restart and is left out.
	/// </summary>
	public static IReadOnlyList<DebugSessionSeen> Read(CallToolResult listing)
	{
		if (listing.IsError == true || listing.StructuredContent is not { ValueKind: JsonValueKind.Object } content) return [];
		if (!content.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array) return [];

		var open = new List<DebugSessionSeen>();

		foreach (var session in sessions.EnumerateArray())
		{
			if (Text(session, "sessionId") is not { } id) continue;

			var ended = string.Equals(Text(session, "state"), nameof(LiveAppSessionState.Ended), StringComparison.OrdinalIgnoreCase);
			if (ended) continue;

			open.Add(new DebugSessionSeen(id, Text(session, "targetDescription") ?? "an unnamed target"));
		}

		return open;
	}

	/// <summary>Numbers a reading about to start.</summary>
	public long Begin() => Interlocked.Increment(ref _started);

	/// <summary>Keeps what reading <paramref name="reading"/> found, unless a later reading or a take came first.</summary>
	public void Record(long reading, IReadOnlyList<DebugSessionSeen> open)
	{
		lock (_gate)
		{
			if (reading <= _newest) return;

			_newest = reading;
			_open = open;
		}
	}

	/// <summary>The sessions last seen open, forgetting them and every reading not yet recorded.</summary>
	public IReadOnlyList<DebugSessionSeen> Take()
	{
		lock (_gate)
		{
			var taken = _open;

			_open = [];
			_newest = Interlocked.Read(ref _started);

			return taken;
		}
	}

	private static string? Text(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
