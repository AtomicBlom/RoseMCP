namespace RoseMcp.Broker;

/// <summary>What one git command printed, or why it never got to.</summary>
public sealed record GitOutcome
{
	/// <summary>Its exit code, or null where it never ran or was stopped.</summary>
	public int? ExitCode { get; init; }

	/// <summary>What it wrote to stdout.</summary>
	public string Output { get; init; } = string.Empty;

	/// <summary>What it wrote to stderr, or why it never ran, for a sentence.</summary>
	public string Error { get; init; } = string.Empty;

	/// <summary>Whether it ran and succeeded.</summary>
	public bool Succeeded => ExitCode == 0;

	/// <summary>An outcome for a command that never produced one.</summary>
	public static GitOutcome Failed(string why) => new() { Error = why };
}
