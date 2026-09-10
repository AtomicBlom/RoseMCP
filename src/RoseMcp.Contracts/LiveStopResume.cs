namespace RoseMcp.Contracts;

/// <summary>
/// What will let a stopped target go again, and therefore what the deadline on a
/// <see cref="LiveStop"/> means.
/// </summary>
public enum LiveStopResume
{
	/// <summary>
	/// The safety timer, which resumes the target so an unattended stop cannot wedge somebody's app.
	/// </summary>
	AutoContinue,

	/// <summary>
	/// A person is looking at this stop, so the safety timer is suspended and the hold's own expiry is
	/// the deadline. A hold is bounded too: a reader who walks away must not leave an app frozen.
	/// </summary>
	HeldByOperator,
}
