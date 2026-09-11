namespace RoseMcp.Contracts;

/// <summary>
/// One place inside a method's source where execution can be stopped: a sequence point, with the
/// method its instructions really belong to.
/// <para>
/// The two are not the same method as often as one would think, which is the whole reason a position
/// is offered rather than a line number. A line inside a lambda compiles into a method of the
/// compiler's own, and a line after an <c>await</c> into a state machine's <c>MoveNext</c>; a
/// breakpoint on either has to be set against that method and not the one somebody was reading.
/// </para>
/// </summary>
public sealed record LiveMethodPosition
{
	/// <summary>
	/// The location to set a breakpoint at, naming the method these instructions are compiled into.
	/// Paired with <see cref="IlOffset"/> it is the exact position; on its own it is that method's
	/// first instruction.
	/// </summary>
	public required string Location { get; init; }

	/// <summary>
	/// That method as a person says it -- <c>Widget.Refresh (lambda)</c> for a line inside one -- so
	/// a reader can see when a click lands somewhere other than the method they are looking at.
	/// </summary>
	public required string DisplayName { get; init; }

	/// <summary>The instruction offset within that method.</summary>
	public required int IlOffset { get; init; }

	public required int Line { get; init; }

	public required int Column { get; init; }

	public required int EndLine { get; init; }

	public required int EndColumn { get; init; }
}
