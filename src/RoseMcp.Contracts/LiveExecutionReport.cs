namespace RoseMcp.Contracts;

/// <summary>
/// What every answer read out of a stop carries: whether the target is still held, which stop the
/// answer was read at, and why it is empty when it is.
/// <para>
/// A stack, a frame's locals, a thread list and an expanded value can only be read while the target
/// is stopped, and it stops being stopped for reasons the caller did not cause -- the safety timer,
/// a hold expiring, an agent continuing. So none of them refuses: each answers with
/// <see cref="LiveExecutionState.Running"/>, an empty result and a <see cref="Detail"/> saying so.
/// A refusal would read as a broken call, and the honest fact is that the question no longer has an
/// answer.
/// </para>
/// <para>
/// <see cref="Stop"/> is echoed rather than left to the caller because everything read from a stop
/// is valid only within it. A reader holding frames from one stop and variables from the next has a
/// view that never existed, and <see cref="LiveStop.EventSequence"/> is what makes that detectable.
/// </para>
/// </summary>
public abstract record LiveExecutionReport
{
	/// <summary>Whether the target is running or held, as of the moment this was read.</summary>
	public required LiveExecutionState Execution { get; init; }

	/// <summary>The stop this was read at. Null exactly when the target is running.</summary>
	public LiveStop? Stop { get; init; }

	/// <summary>
	/// Why the answer is empty or partial, when it is. Null when the report says what was asked.
	/// </summary>
	public string? Detail { get; init; }
}
