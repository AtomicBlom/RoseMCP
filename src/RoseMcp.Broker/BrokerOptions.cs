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

	/// <summary>
	/// How long a freshly started worker has to complete its MCP handshake.
	/// <para>
	/// Set explicitly because the SDK's own default is 60 seconds, and a worker begins loading its
	/// solution the moment the process starts -- deliberately, so the design-time build overlaps the
	/// handshake, but it means the two compete for the machine. With several workers doing that at
	/// once, a cold one loses 60 seconds and the call fails rather than waits, reporting a timeout
	/// that names the SDK and says nothing about load. Slow is the honest answer there; failing is
	/// not, because nothing is wrong.
	/// </para>
	/// <para>
	/// Long rather than unbounded. A worker whose process died takes its transport with it and fails
	/// immediately, so this only governs one that is alive and silent, and that should be noticed
	/// rather than waited on forever.
	/// </para>
	/// </summary>
	public TimeSpan WorkerHandshakeTimeout { get; set; } = TimeSpan.FromMinutes(3);
}
