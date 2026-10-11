using System.ComponentModel;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;

namespace RoseMcp.Broker.Tools;

/// <summary>
/// The agent's side of a note a person sent it from a window: answering it. Answered by the broker alone, about no
/// workspace.
/// <para>
/// An agent can answer and do nothing else: sending a note, changing one or reading another session's is the
/// person's, on the operator API behind the token, because a note reaches an agent as the person's words. So the
/// one tool answers only a note sent to the session calling it, found by the key the call filter makes ambient, and
/// a note of any other session is answered as one that does not exist.
/// </para>
/// <para>
/// Not routed by the instructions: the note's own notice names this tool and the id it takes, and a session has no
/// use for it before a note arrives.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class NoteTools(AgentNotes notes)
{
	/// <summary>
	/// Adds an answer under a note. Not read-only, since the person's window shows it; not idempotent, since a
	/// second call is a second answer rather than the same one again.
	/// </summary>
	[McpServerTool(
		Name = ToolNames.Reply,
		Title = "Answer a note from the person",
		ReadOnly = false,
		Destructive = false,
		Idempotent = false,
		OpenWorld = false,
		UseStructuredContent = true)]
	[Description(ToolDescriptions.Reply)]
	public NoteReplied Reply(
		[Description(ToolDescriptions.NoteIdArgument)] string noteId,
		[Description(ToolDescriptions.ReplyTextArgument)] string text)
	{
		var caller = AgentNotes.Caller;

		return notes.Reply(caller?.Relayed, caller?.TransportSession, noteId, text, CallRecording.Current?.Id);
	}
}
