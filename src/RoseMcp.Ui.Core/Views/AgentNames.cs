using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// What an agent session is called in a window: the label whatever started it gave it, else the folder it works in,
/// else what its client calls itself. The same rule for a client in the table and for an entry in the record, so a
/// card and the calls under it, and a workspace's "used by", name one session one way.
/// </summary>
public static class AgentNames
{
	/// <summary>What a client nothing names is called: one that reached the broker over http without the relay.</summary>
	public const string Unnamed = "an unnamed client";

	/// <summary>A client in the broker's table.</summary>
	public static string Of(ClientSummary client)
	{
		if (!client.Named) return Unnamed;

		return Choose(client.Label, client.WorkingDirectory ?? client.Roots.FirstOrDefault(), client.ClientName);
	}

	/// <summary>The client an entry of the record names, as it was when the call ended.</summary>
	public static string Of(ActivityEntry entry) => Choose(entry.ClientLabel, entry.Origin, entry.ClientName);

	/// <summary>The last part of a folder, which is what tells two worktrees of one repository apart.</summary>
	public static string Leaf(string folder)
	{
		var trimmed = folder.TrimEnd('/', '\\');
		var cut = trimmed.LastIndexOfAny(['/', '\\']);

		return cut < 0 ? trimmed : trimmed[(cut + 1)..];
	}

	private static string Choose(string? label, string? folder, string? clientName)
	{
		if (label is { Length: > 0 }) return label;
		if (folder is { Length: > 0 }) return Leaf(folder);

		return clientName is { Length: > 0 } ? clientName : Unnamed;
	}
}
