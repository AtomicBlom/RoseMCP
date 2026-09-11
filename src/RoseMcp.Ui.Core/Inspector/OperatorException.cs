using System.Net;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Why a call to the operator API did not answer. Five outcomes rather than one, because each
/// sends the reader somewhere different and a window that says "request failed" has told them
/// nothing they can act on.
/// </summary>
public enum OperatorFailure
{
	/// <summary>Nothing is listening. The tray is not running, or is on another port.</summary>
	Unreachable,

	/// <summary>The token was refused, which almost always means the tray has restarted.</summary>
	Unauthorized,

	/// <summary>The session, breakpoint or element is not there any more.</summary>
	NotFound,

	/// <summary>
	/// The host understood and said no: a bad argument, a frame index past the end of a stack, a
	/// XAML request into a stopped target. The message is the host's own and is worth showing.
	/// </summary>
	Refused,

	/// <summary>The request took longer than its budget. Distinct from a caller giving up.</summary>
	TimedOut,

	/// <summary>Anything else, including a 500 the host could not explain.</summary>
	Failed,
}

/// <summary>
/// A call to the operator API that did not answer, carrying the host's own words where it had any.
/// <para>
/// The message is the host's sentence rather than a status line, because the operator API forwards
/// real messages for exactly this reason -- an error says what went wrong, not that something did.
/// </para>
/// </summary>
public sealed class OperatorException(OperatorFailure failure, string message, HttpStatusCode? status = null, Exception? inner = null)
	: Exception(message, inner)
{
	public OperatorFailure Failure { get; } = failure;

	/// <summary>The status the tray answered with, or null when nothing answered at all.</summary>
	public HttpStatusCode? Status { get; } = status;
}
