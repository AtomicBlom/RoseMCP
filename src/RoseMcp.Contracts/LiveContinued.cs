namespace RoseMcp.Contracts;

/// <summary>
/// What <c>rose_debug_continue</c> did, and to which session.
/// <para>
/// The agent-facing counterpart of <see cref="LiveContinueResult"/>, which is what the host answers
/// with and knows no session id: the host serves one session, and the id is the broker's name for it.
/// The broker names the session here rather than on the host's type, so the operator API and the
/// inspector, which read that type, are not given a field the host never fills.
/// </para>
/// <para>
/// A sentence saying the target resumed names no session, which is the one thing a caller holding
/// several cannot check, and gives an agent nothing to branch on but the wording.
/// </para>
/// </summary>
public sealed record LiveContinued : LiveResult
{
	/// <summary>The session that was asked to continue.</summary>
	public required string SessionId { get; init; }

	/// <summary>
	/// Whether a target was held and has been resumed. False is not a failure in the usual case:
	/// nothing was stopped, which is what makes continuing speculatively safe. The host answers false
	/// too when the debugger refused the resume, which it logs rather than reports.
	/// </summary>
	public required bool Continued { get; init; }

	/// <summary>
	/// What else the caller should know about the resume, or null when there is nothing -- such as a
	/// hold a person had placed being released by it. See <see cref="LiveContinueResult.Detail"/>.
	/// </summary>
	public string? Detail { get; init; }
}
