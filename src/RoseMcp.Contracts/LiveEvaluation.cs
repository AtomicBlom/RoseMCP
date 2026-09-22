namespace RoseMcp.Contracts;

/// <summary>
/// The result of evaluating a simple expression against a stopped frame. The evaluator reads
/// field-access chains -- an argument or local, then <c>.field</c> into the object graph -- directly
/// from memory, without running any of the debuggee's own code, so it cannot hang or corrupt the
/// target the way a method-call evaluation could. <see cref="Error"/> is set (and the value fields
/// null) when the target is not stopped, the root name is not in the frame, or a field does not exist.
/// </summary>
public sealed record LiveEvaluation : LiveResult
{
	public required string Expression { get; init; }

	public string? TypeName { get; init; }

	public string? Value { get; init; }

	/// <summary>
	/// How long the value really is, when <see cref="Value"/> holds only the start of it. Null means
	/// what is here is all of it. Ask again with a larger <c>maxLength</c> to read more; this says
	/// how much larger it would have to be.
	/// </summary>
	public int? FullLength { get; init; }

	public string? Error { get; init; }
}
