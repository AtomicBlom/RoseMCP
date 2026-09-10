namespace RoseMcp.Contracts;

/// <summary>The outcome of a continue request: whether a target was actually held and resumed.</summary>
public sealed record LiveContinueResult
{
	public required bool Continued { get; init; }

	/// <summary>
	/// What else the caller should know about the resume, or null when there is nothing.
	/// <para>
	/// It carries the one outcome that is a success and still a surprise: a resume issued while a
	/// person was holding the stop releases that hold. Refusing instead would be worse -- an agent's
	/// continue means continue -- but a hold that vanishes silently is a reader's stack disappearing
	/// with nothing to say why.
	/// </para>
	/// </summary>
	public string? Detail { get; init; }
}
