namespace RoseMcp.Contracts;

/// <summary>
/// What <c>rose_debug_step</c> did, and to which session.
/// <para>
/// The step's counterpart of <see cref="LiveContinued"/>, and the broker's rather than the host's for
/// the same reason. Where the step lands is not here: the target is resumed briefly and held again,
/// and the new location arrives as a <see cref="LiveDebugEventKind.StepComplete"/> event after
/// <see cref="LiveResult.Cursor"/>, which is the cursor to wait past.
/// </para>
/// </summary>
public sealed record LiveStepped : LiveResult
{
	/// <summary>The session that was asked to step.</summary>
	public required string SessionId { get; init; }

	/// <summary>The direction asked for: in, over or out.</summary>
	public required string Mode { get; init; }

	/// <summary>
	/// Whether a target was held and the step was issued. False is not a failure in the usual case:
	/// nothing was stopped, so there was nothing to step from. The host answers false too when the
	/// debugger refused the step, which it logs rather than reports. A mode that is none of the three is
	/// refused rather than answered false.
	/// </summary>
	public required bool Stepped { get; init; }

	/// <summary>
	/// What else the caller should know about the step, or null when there is nothing. See
	/// <see cref="LiveContinueResult.Detail"/>.
	/// </summary>
	public string? Detail { get; init; }
}
