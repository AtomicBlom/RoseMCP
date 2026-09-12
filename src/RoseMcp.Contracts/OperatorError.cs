namespace RoseMcp.Contracts;

/// <summary>
/// What an operator endpoint returns when it could not do what was asked.
/// <para>
/// The real message travels, which is the same rule the MCP boundary follows and for the same
/// reason: a caller told only that something failed has to guess, and the guesses are expensive.
/// The status separates the kinds a client acts on differently -- a refusal it can fix by asking
/// differently, a session that is gone and should be dropped from a list, a token that will not work
/// however many times it is retried.
/// </para>
/// </summary>
public sealed record OperatorError
{
	/// <summary>What went wrong, in the words of whatever knew.</summary>
	public required string Message { get; init; }

	/// <summary>The http status this came back with, so a deserialised error still carries it.</summary>
	public required int Status { get; init; }
}
