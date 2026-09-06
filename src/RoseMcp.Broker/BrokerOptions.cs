namespace RoseMcp.Broker;

/// <summary>Where to find the worker executable, and how workers are configured.</summary>
public sealed class BrokerOptions
{
	/// <summary>
	/// Explicit path to the worker executable. When unset it is taken from the <c>ROSEMCP_WORKER</c>
	/// environment variable, then discovered next to the broker, and failing that in the sibling project
	/// output so the repository works without being published first.
	/// </summary>
	public string? WorkerPath { get; set; }

	/// <summary>Passed through to every worker.</summary>
	public bool NoRestore { get; set; }
	/// <summary>
	/// Where to look for a solution when nothing else in the call resolved one -- the last step of
	/// <see cref="WorkspaceManager.WorkspaceFor"/>, after the workspace argument, the paths the call
	/// carries and the calling session's directory. What is already open is deliberately not part of that
	/// decision. Defaults to the process working directory, which for an MCP server launched by an editor
	/// is the project root, and is what makes every tool work with no setup call first.
	/// </summary>
	public string DefaultWorkspaceRoot { get; set; } = Environment.CurrentDirectory;

	/// <summary>How long to wait for a worker to finish loading before giving up on it.</summary>
	public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(10);
}
