namespace RoseMcp.Contracts;

/// <summary>
/// One edit a live edit made to a debugged app, as the broker recorded it for the session: when, who asked for it,
/// the element it landed on and the property it changed, from what to what. Recorded only for an edit that took,
/// since a change that did not happen is not one a person should be shown in the app.
/// <para>
/// Kept for the session, in memory, the newest last, and gone with the session: a handle means nothing to another
/// run of the app, and the app itself forgets a live edit the moment it rebuilds that part of its UI.
/// </para>
/// </summary>
public sealed record LiveXamlChange
{
	/// <summary>Counts the session's changes from one, so two made in the same instant are still told apart and ordered.</summary>
	public required long Sequence { get; init; }

	/// <summary>When the broker saw the apply answer.</summary>
	public required DateTime ChangedUtc { get; init; }

	/// <summary>Whether the person made it, from a window, rather than an agent session.</summary>
	public required bool Person { get; init; }

	/// <summary>
	/// The agent session that made it, by its row in the client table now; null for the person, and for an agent
	/// session the table no longer lists.
	/// </summary>
	public LiveAppAgent? Agent { get; init; }

	/// <summary>The tool that made it, as the activity record files the call: <c>rose_xaml_apply</c>.</summary>
	public required string Tool { get; init; }

	/// <summary>What kind of edit: SetProperty, ClearProperty, AddChild, RemoveChild or SetResource.</summary>
	public required string Kind { get; init; }

	/// <summary>The element as the edit addressed it, which is what the markup calls it.</summary>
	public required string Target { get; init; }

	/// <summary>The element's handle in the live tree, which is how a window finds its row; null where the provider named none.</summary>
	public ulong? Handle { get; init; }

	/// <summary>The property changed, or the resource key replaced; null for an edit that changes no property.</summary>
	public string? Property { get; init; }

	/// <summary>What the property said before; see <see cref="LiveXamlEditResult.OldValue"/>.</summary>
	public string? OldValue { get; init; }

	/// <summary>What the edit set it to; null where it cleared it, or changed no property.</summary>
	public string? NewValue { get; init; }

	/// <summary>The markup file the apply named, where it named one.</summary>
	public string? File { get; init; }
}
