using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Ui.Core.Views;

/// <summary>What the RoseMCP window says in words, here so a test can read them without standing up a window.</summary>
public static class RoseText
{
	/// <summary>What the window says when nothing is connected.</summary>
	public const string NoAgents =
		"No agent is connected. An agent connects through the relay that RoseMCP's setup registers with it; "
			+ "one that connects over http without the relay shows here as unnamed.";

	/// <summary>What the Workspaces view says when nothing is loaded.</summary>
	public const string NoWorkspaces =
		"No workspace is loaded. A workspace opens the first time an agent asks about a solution, and closes again "
			+ "once nobody has used it for a while.";

	/// <summary>What the Debugging view says when nothing is being debugged.</summary>
	public const string NoSessions =
		"Nothing is being debugged. An agent attaches with rose_debug_attach or launches with rose_debug_launch; "
			+ "Debug an app opens an Inspector window that takes the one session there is.";

	/// <summary>
	/// Why a read failed, said for a window that keeps trying: what is wrong and what will put it right, never only
	/// that something did.
	/// </summary>
	/// <param name="exception">What the read threw.</param>
	/// <param name="hasToken">Whether the window holds a token at all.</param>
	/// <param name="baseAddress">Where the window reads Rose.</param>
	public static string Problem(Exception exception, bool hasToken, Uri baseAddress) => exception switch
	{
		OperatorException { Failure: OperatorFailure.Unauthorized } when !hasToken =>
			"This window was opened without Rose's operator token. Open RoseMCP from the tray icon, which hands it over.",
		OperatorException { Failure: OperatorFailure.Unauthorized } =>
			"Rose has restarted since this window was opened, and the new run has a token of its own. Click the RoseMCP "
				+ "icon in the notification area to reconnect this window.",
		OperatorException { Failure: OperatorFailure.Unreachable } =>
			$"Rose is not serving at {baseAddress.GetLeftPart(UriPartial.Authority)}. This window reconnects by itself "
				+ "when it is back; if it does not come back, the tray icon says why.",
		_ => exception.Message,
	};
}
