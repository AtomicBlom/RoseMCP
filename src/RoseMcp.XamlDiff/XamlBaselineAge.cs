namespace RoseMcp.XamlDiff;

/// <summary>
/// What can be said about a XAML file's last write against the moment the target started running.
/// <para>
/// Three states rather than a bool, because "cannot tell" is a real outcome here -- the process may
/// have exited, or refuse to say when it started -- and the note the caller reads differs. Reporting
/// an unreadable start time as "changed" would be a claim about the file with nothing behind it.
/// </para>
/// </summary>
public enum XamlBaselineAge
{
	Unknown,
	UnchangedSinceTargetStarted,
	ChangedSinceTargetStarted,
}
