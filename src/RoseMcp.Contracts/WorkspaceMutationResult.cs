namespace RoseMcp.Contracts;

/// <summary>
/// A result from an operation that writes, or would write, source files.
/// <para>
/// Separate from a plain <see cref="WorkspaceScopedResult"/> because a change has a consequence a
/// read does not: the files it touched may belong to projects that other solutions in the same
/// tree also build, and the operation only ever saw one solution. Naming what was written is what
/// lets the broker work that out without every tool having to.
/// </para>
/// </summary>
public abstract record WorkspaceMutationResult : WorkspaceScopedResult
{
	/// <summary>
	/// Absolute paths this operation wrote, or would write when it was only a preview.
	/// </summary>
	public IReadOnlyList<string> ChangedFiles { get; init; } = [];

	/// <summary>
	/// Caveats worth reading before trusting the change: markup the compiler cannot see, a sibling
	/// solution that shares these files, anything the operation deliberately left alone.
	/// </summary>
	public IReadOnlyList<string> Notices { get; init; } = [];
}
