namespace RoseMcp.Contracts;

/// <summary>
/// The outcome of taking or releasing an operator's hold on a stop.
/// <para>
/// A hold suspends the safety timer so a stack does not move while somebody reads it. The stop it
/// returns is the authority on what happens next: <see cref="LiveStop.Resume"/> says whether the
/// timer or the hold decides, and <see cref="LiveStop.ResumeDeadlineUtc"/> says when.
/// </para>
/// </summary>
public sealed record LiveHoldResult : LiveExecutionReport
{
	/// <summary>
	/// Whether the request changed anything. False when there was nothing stopped to hold, or when
	/// a release was asked for and no hold was in place.
	/// </summary>
	public required bool Applied { get; init; }
}
