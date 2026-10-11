namespace RoseMcp.Ui.Core.Views;

/// <summary>An agent session a note can be sent to: its row in the broker's client table, and what a window calls it.</summary>
/// <param name="Id">The client's row id, which a note names its client by.</param>
/// <param name="Name">What the composer calls it: its label, else its folder, else its client's name.</param>
public sealed record NoteRecipient(string Id, string Name);
