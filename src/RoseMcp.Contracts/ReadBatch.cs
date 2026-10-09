namespace RoseMcp.Contracts;

/// <summary>
/// What a read that takes a list answered for each thing it was asked about. <see cref="Results"/> is
/// one entry per request, in the order they were given; <see cref="Found"/> counts the ones answered.
/// A request refused for its own reason -- a name nothing declares, an overload not picked -- is that
/// entry's status and never stops the others, because the requests are independent and a partial
/// answer the caller can read beats an all-or-nothing refusal it has to retry piece by piece.
/// <see cref="Notices"/> carries what is true of the whole call rather than of one entry.
/// </summary>
/// <typeparam name="T">What one request is answered with.</typeparam>
public sealed record ReadBatch<T> : WorkspaceScopedResult
	where T : class
{
	public required long Revision { get; init; }

	/// <summary>How many requests were answered.</summary>
	public int Found => Results.Count(entry => entry.Answer is not null);

	/// <summary>How many were asked about.</summary>
	public int Total => Results.Count;

	public required IReadOnlyList<ReadEntry<T>> Results { get; init; }

	/// <summary>
	/// What the caller should know about the call as a whole, such as what the workspace reconciled
	/// before reading. A notice about one request is on that request's answer.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];

	/// <summary>
	/// The directory a relative <c>filePath</c> in the answers is measured from: the one the calling
	/// session runs in, which is also what a relative path sent back is measured from, so it names the
	/// same file in the next call. Absent where every path is absolute.
	/// </summary>
	public string? RelativeTo { get; init; }
}

/// <summary>
/// One request of a <see cref="ReadBatch{T}"/> and what became of it. <see cref="Status"/> is
/// <c>found</c> where there is an <see cref="Answer"/>, or <c>refused: </c> and the reason there is none.
/// </summary>
/// <typeparam name="T">What the request is answered with.</typeparam>
public sealed record ReadEntry<T>
	where T : class
{
	/// <summary>What was asked about, as the caller wrote it, which is how an entry is matched to its request.</summary>
	public required string Requested { get; init; }

	public required string Status { get; init; }

	/// <summary>The answer, or null where the request was refused.</summary>
	public T? Answer { get; init; }
}
