using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// A live edit as the XAML view says it: what a property went from and to, and who changed it, with what and when.
/// One place, so the tree's mark, a property's line and a note to an agent say a change the same way.
/// </summary>
public static class XamlChangeText
{
	/// <summary>What a value that was never read is shown as, which is not the same as a value that was empty.</summary>
	public const string Unread = "(not read)";

	/// <summary>What a property an edit cleared goes to, which is whatever the framework gives it, not a value of the edit's.</summary>
	public const string Cleared = "(cleared)";

	/// <summary>The value before and after: <c>12,0,12,0 → 8,0,8,0</c>.</summary>
	public static string Values(LiveXamlChange change)
	{
		ArgumentNullException.ThrowIfNull(change);

		var after = change.Kind == "ClearProperty" ? Cleared : Shown(change.NewValue);
		return $"{Shown(change.OldValue)} \u2192 {after}";
	}

	/// <summary>
	/// Who changed it, with what and when: <c>D:\Drawboard\PDF · rose_xaml_apply · 14:05</c>. An agent session by
	/// what the client table calls it now, and one it no longer lists said as gone rather than left blank.
	/// </summary>
	public static string By(LiveXamlChange change)
	{
		ArgumentNullException.ThrowIfNull(change);

		var who = change.Person
			? "you"
			: change.Agent is { } agent
				? agent.Name ?? agent.ClientId
				: "an agent session no longer connected";

		var when = change.ChangedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
		return $"{who}{Format.Separator}{change.Tool}{Format.Separator}{when}";
	}

	/// <summary>One change in a line, as an element's mark lists them: <c>Margin 12,0,12,0 → 8,0,8,0</c>.</summary>
	public static string Line(LiveXamlChange change)
	{
		ArgumentNullException.ThrowIfNull(change);

		return change.Kind switch
		{
			"SetProperty" or "ClearProperty" => $"{change.Property} {Values(change)}",
			"AddChild" => $"an element was added to {change.Target}",
			"RemoveChild" => $"{change.Target} was removed",
			"SetResource" => $"resource {change.Property} was replaced",
			_ => $"{change.Kind} {change.Property}".Trim(),
		};
	}

	/// <summary>An element's changes, newest first, one a line, for the mark's tooltip.</summary>
	public static string Note(IEnumerable<LiveXamlChange> changes) =>
		string.Join(Environment.NewLine, changes.OrderByDescending(change => change.Sequence).Select(change => $"{Line(change)} ({By(change)}{From(change)})"));

	/// <summary>
	/// The older changes a list no longer holds, said where there are any, since a list that starts part way through
	/// otherwise reads as everything that was done.
	/// </summary>
	public static string Dropped(long dropped) =>
		dropped <= 0 ? string.Empty : $"{Format.Count((int)Math.Min(dropped, int.MaxValue), "older change")} this session are no longer kept.";

	/// <summary>The markup file the apply named, as a file name, where it named one.</summary>
	private static string From(LiveXamlChange change) =>
		change.File is { Length: > 0 } file ? $", from {Path.GetFileName(file)}" : string.Empty;

	private static string Shown(string? value) => value switch
	{
		null => Unread,
		"" => "\"\"",
		_ => value,
	};
}
