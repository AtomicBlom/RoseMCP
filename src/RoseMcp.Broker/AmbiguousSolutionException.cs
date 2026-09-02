using ModelContextProtocol;

namespace RoseMcp.Broker;

/// <summary>
/// More than one solution shares a directory and nothing decides between them.
/// <para>
/// An <see cref="McpException"/> because its message is the entire value of it: the SDK turns an
/// unrecognised exception into "An error occurred invoking 'rose_diagnostics'." and drops the
/// message, and a caller that could have fixed the call itself is then told nothing. This one knows
/// the candidates and what to do about them, so it says both.
/// </para>
/// <para>
/// Refusing is deliberate, and it is the one place this server asks a question rather than
/// answering. Everywhere else a missing argument is inferred, because a tool that needs setup
/// before its first useful answer loses to grep. Here inference is what produced the bug: guessing
/// costs a whole design-time build of the wrong solution and returns an empty result shaped exactly
/// like a true negative, which is far more expensive than one round trip.
/// </para>
/// </summary>
public sealed class AmbiguousSolutionException(
	string directory,
	IReadOnlyList<string> candidates,
	IReadOnlyList<string> containing)
	: McpException(Describe(directory, candidates, containing))
{
	/// <summary>Every solution in the directory, in the order they were shown to the caller.</summary>
	public IReadOnlyList<string> Candidates { get; } = candidates;

	/// <summary>The directory holding them.</summary>
	public string Directory { get; } = directory;

	private static string Describe(
		string directory,
		IReadOnlyList<string> candidates,
		IReadOnlyList<string> containing)
	{
		var names = string.Join(", ", candidates.Select(Path.GetFileName));

		var why = containing.Count > 1
			? $"{containing.Count} of them compile that path, so it does not say which you mean"
			: "none of them singles out that path";

		return $"{candidates.Count} solutions share {directory} and {why}: {names}. "
			+ "Pass the workspace argument naming the one you mean, or pin it for good with a "
			+ $"\"solution\" entry in {Path.Combine(directory, "rosemcp.json")}.";
	}
}
