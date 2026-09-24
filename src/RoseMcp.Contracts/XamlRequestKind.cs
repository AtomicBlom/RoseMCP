namespace RoseMcp.Contracts;

/// <summary>
/// Whether a request to the XAML provider changes the app, and what a caller is owed when one that
/// does goes unanswered.
/// <para>
/// A bound on a provider request is a bound on how long the host waits, and nothing more. The frame
/// is in the pipe before the wait starts, the provider serves every verb on the app's UI thread, and
/// nothing on this side can cancel work already handed to that thread -- so a mutating request the
/// host has given up on can still take effect, and a later read can observe it. Saying so is the
/// difference between a caller that knows its edit is in doubt and one told plainly that it failed.
/// </para>
/// <para>
/// The list here is of <em>reads</em>, so that the default is to warn. A verb added later and not
/// classified at all is treated as mutating, which at worst puts a sentence on a timed-out read; the
/// other default loses the warning on exactly the verb that needed it, in silence.
/// </para>
/// <para>
/// Here rather than beside the host because there it would be internal to <c>RoseMcp.LiveApp</c>,
/// where no test reaches it: the unit suite does not reference the host at all, and the one test
/// project that does, <c>RoseMcp.IntegrationTests.Windows</c>, sees only its public surface and runs
/// only on Windows. A rule kept there is a rule nothing checks.
/// </para>
/// </summary>
public static class XamlRequestKind
{
	/// <summary>
	/// The verbs that only report. <c>properties</c> is one of them despite bringing a control's
	/// untouched collection properties into existence as it walks them: what that changes is what a
	/// second read of the same element calls set, which the property tool states for every read, and
	/// not anything the app shows or a later request acts on.
	/// </summary>
	private static readonly string[] Reads = ["tree", "properties", "selection"];

	/// <summary>
	/// The sentence a mutating request owes its caller when the wait for its reply expired. Names the
	/// mechanism rather than the possibility alone, because "may still take effect" invites a retry
	/// and the reason it must not be retried is the same reason it may still land.
	/// </summary>
	public const string MayStillLand =
		"The request is already in the pipe and the provider serves it on the app's UI thread, which "
		+ "nothing here can cancel, so it may still take effect after this answer.";

	/// <summary>
	/// Whether <paramref name="request"/> changes the app, and so whether a caller whose wait expired
	/// has to be told the request may still land.
	/// </summary>
	/// <param name="request">A provider request as it goes on the wire, arguments and all.</param>
	public static bool Mutates(string request) => !Reads.Contains(Verb(request));

	/// <summary>
	/// What to append to the message for a <paramref name="request"/> that went unanswered: the
	/// sentence above for a verb that changes the app, and nothing at all for one that only reports.
	/// Composed here rather than by the caller so that what a timed-out request says is decided in
	/// the one place that knows which kind it was.
	/// </summary>
	public static string Caveat(string request) => Mutates(request) ? $" {MayStillLand}" : string.Empty;

	/// <summary>
	/// The verb <paramref name="request"/> starts with. Arguments follow it on the same line
	/// (<c>selecthandle 12</c>, <c>select rulers all</c>) or on the lines after it (<c>apply</c>),
	/// so both separators end the verb.
	/// </summary>
	private static string Verb(string request)
	{
		var end = request.AsSpan().IndexOfAny(' ', '\n');
		return end < 0 ? request : request[..end];
	}
}
