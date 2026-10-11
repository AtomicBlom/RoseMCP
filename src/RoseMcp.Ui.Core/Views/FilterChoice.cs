using RoseMcp.Contracts;
using RoseMcp.Solutions;

namespace RoseMcp.Ui.Core.Views;

/// <summary>One choice in one of the Activity view's filters: what it says, and what the record is asked for.</summary>
/// <param name="Label">What the list shows.</param>
/// <param name="Value">What the filter sends; null for "any".</param>
public sealed record FilterChoice(string Label, string? Value)
{
	/// <summary>
	/// The agents a person can narrow the record to: every agent the newest entries name, newest first, by the key
	/// its calls carry -- which outlives a broker restart, so one choice covers an agent's calls on both sides of it.
	/// </summary>
	public static IReadOnlyList<FilterChoice> Agents(IEnumerable<ActivityEntry> entries) =>
		[
			new FilterChoice("Any agent", null),
			.. entries
				.Where(entry => entry.Kind == ActivityEntryKind.Call && (entry.ClientKey ?? entry.ClientId) is { Length: > 0 })
				.OrderByDescending(entry => entry.StartedUtc)
				.DistinctBy(entry => entry.ClientKey ?? entry.ClientId, StringComparer.Ordinal)
				.Select(entry => new FilterChoice(AgentNames.Of(entry), entry.ClientKey ?? entry.ClientId)),
		];

	/// <summary>
	/// The workspaces a person can narrow the record to: every one loaded now, and every one the newest entries name,
	/// each once however its path is spelled.
	/// </summary>
	public static IReadOnlyList<FilterChoice> Workspaces(IEnumerable<OperatorWorkspace> loaded, IEnumerable<ActivityEntry> entries) =>
		[
			new FilterChoice("Any workspace", null),
			.. loaded
				.Select(workspace => workspace.Summary.Workspace)
				.Concat(entries.OrderByDescending(entry => entry.StartedUtc).Select(entry => entry.Workspace))
				.OfType<string>()
				.Where(path => path.Length > 0)
				.Distinct(PathCasing.Comparer)
				.Select(path => new FilterChoice($"{Format.FileLine(path, line: null)}{Format.Separator}{AgentNames.Leaf(FolderOf(path))}", path)),
		];

	/// <summary>The folder part of a path written by either operating system, since it is the broker's path rather than this machine's.</summary>
	private static string FolderOf(string path)
	{
		var cut = path.LastIndexOfAny(['/', '\\']);

		return cut <= 0 ? path : path[..cut];
	}
}
