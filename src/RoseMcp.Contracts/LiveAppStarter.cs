namespace RoseMcp.Contracts;

/// <summary>
/// Who started a debug session, which decides who it belongs to: an agent's is its own and the person reaches it
/// from a window, while a person's is theirs alone until they share it with one agent session.
/// </summary>
public enum LiveAppStarter
{
	/// <summary>An agent, through <c>rose_debug_attach</c>, <c>rose_debug_launch</c> or <c>rose_debug_launch_uwp</c>.</summary>
	Agent,

	/// <summary>The person running the broker, from a window, through the operator API.</summary>
	Person,
}
