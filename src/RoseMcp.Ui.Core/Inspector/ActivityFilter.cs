using System.Globalization;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Which entries of the activity record a window asks <see cref="OperatorClient.ActivityAsync"/> for. Every filter
/// left null matches everything, as the route reads it.
/// </summary>
public sealed record ActivityFilter
{
	/// <summary>A client, by the id the client table gives it or the key an entry carries.</summary>
	public string? Client { get; init; }

	/// <summary>A workspace, by its solution path or its key.</summary>
	public string? Workspace { get; init; }

	/// <summary>A debug session, by its id.</summary>
	public string? Session { get; init; }

	/// <summary>A tool, by its name.</summary>
	public string? Tool { get; init; }

	/// <summary>Only entries that started at or after this.</summary>
	public DateTimeOffset? Since { get; init; }

	/// <summary>Only entries that started before this.</summary>
	public DateTimeOffset? Until { get; init; }

	/// <summary>Only calls that failed.</summary>
	public bool Failures { get; init; }

	/// <summary>Only entries naming this in what a listing carries, ignoring case.</summary>
	public string? Text { get; init; }

	/// <summary>How many entries a page holds at most; the broker's default where null.</summary>
	public int? Limit { get; init; }

	/// <summary>Whether each entry carries its arguments, response and diffs.</summary>
	public bool Bodies { get; init; }

	/// <summary>The filter as a query string's arguments, each escaped, the unset ones left out.</summary>
	public IEnumerable<string> Arguments()
	{
		if (Client is { } client) yield return $"client={Uri.EscapeDataString(client)}";
		if (Workspace is { } workspace) yield return $"workspace={Uri.EscapeDataString(workspace)}";
		if (Session is { } session) yield return $"session={Uri.EscapeDataString(session)}";
		if (Tool is { } tool) yield return $"tool={Uri.EscapeDataString(tool)}";
		if (Since is { } since) yield return $"since={Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))}";
		if (Until is { } until) yield return $"until={Uri.EscapeDataString(until.ToString("O", CultureInfo.InvariantCulture))}";
		if (Failures) yield return "failures=true";
		if (Text is { Length: > 0 } text) yield return $"text={Uri.EscapeDataString(text)}";
		if (Limit is { } limit) yield return $"limit={limit}";
		if (Bodies) yield return "bodies=true";
	}
}
