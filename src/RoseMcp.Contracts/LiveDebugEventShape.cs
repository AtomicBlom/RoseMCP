namespace RoseMcp.Contracts;

/// <summary>
/// How the events past a page divide by kind and by exception type. Every group is keyed by the value
/// the narrowing argument of the same name takes -- a kind is one of <c>kinds</c>, an exception type
/// is <c>exceptionType</c> -- so a group read here is a question the next call can ask.
/// </summary>
public sealed record LiveDebugEventShape
{
	/// <summary>How many matching events are buffered past the page.</summary>
	public required int Total { get; init; }

	/// <summary>Every kind among them, most first.</summary>
	public required IReadOnlyList<LiveEventKindCount> Kinds { get; init; }

	/// <summary>Every exception type among them, most first.</summary>
	public required IReadOnlyList<LiveExceptionTypeCount> ExceptionTypes { get; init; }
}

/// <summary>How many events are of one kind.</summary>
public sealed record LiveEventKindCount
{
	public required string Kind { get; init; }

	public required int Count { get; init; }
}

/// <summary>How many exception events carry one exception type.</summary>
public sealed record LiveExceptionTypeCount
{
	public required string ExceptionType { get; init; }

	public required int Count { get; init; }
}
