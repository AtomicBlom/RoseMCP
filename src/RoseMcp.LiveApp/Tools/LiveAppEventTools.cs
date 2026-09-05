using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Tools;

/// <summary>The buffered debug events this host has captured, read by the broker for the agent.</summary>
[McpServerToolType]
public sealed class LiveAppEventTools(LiveAppSessionHost host)
{
	[McpServerTool(
		Name = ToolNames.LiveAppEvents,
		Title = "Live-app debug events",
		ReadOnly = true,
		Idempotent = true,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description("Buffered debug events with a sequence above the given cursor, and the session's state.")]
	public Task<LiveDebugEventPage> Events(
		[Description("Return only events whose sequence is greater than this; 0 for everything buffered.")]
		long after = 0,
		[Description("Comma-separated event kinds to return; empty for all.")]
		string? kinds = null,
		[Description("Maximum events to return in this page.")]
		int limit = 500,
		[Description("Seconds to wait for a matching event before answering; 0 answers at once.")]
		int waitSeconds = 0,
		CancellationToken cancellationToken = default)
		=> host.ReadEventsAsync(after, ParseKinds(kinds), limit, waitSeconds, cancellationToken);

	/// <summary>
	/// Parses the kind filter, ignoring anything it does not recognise rather than failing the read.
	/// A misspelt kind that emptied the filter would silently widen the answer instead of narrowing
	/// it, so an unrecognised name is dropped and the ones that parsed still apply; a filter that
	/// parses to nothing at all is treated as no filter, which is what an empty string means anyway.
	/// </summary>
	private static IReadOnlyCollection<LiveDebugEventKind>? ParseKinds(string? kinds)
	{
		if (string.IsNullOrWhiteSpace(kinds)) return null;

		var parsed = new HashSet<LiveDebugEventKind>();
		foreach (var name in kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (Enum.TryParse<LiveDebugEventKind>(name, ignoreCase: true, out var kind)) parsed.Add(kind);
		}

		return parsed.Count == 0 ? null : parsed;
	}
}
