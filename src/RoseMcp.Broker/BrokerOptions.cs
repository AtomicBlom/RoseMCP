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
	/// Whether <see cref="DefaultWorkspaceRoot"/> is where the caller stands when a call does not say:
	/// true for a stdio broker, whose client chose its working directory, and false for an http one,
	/// whose own directory is the tray's or the server's and holds none of the caller's files. A result's
	/// paths are made relative only to a directory known to be the caller's, so this is off unless the
	/// host that knows says otherwise.
	/// </summary>
	public bool DefaultRootIsTheCaller { get; set; }

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

	/// <summary>
	/// What a long-lived broker -- the tray, or the server over http -- sets
	/// <see cref="IdleEvictionAfter"/> to. Long enough that a session stepping away for a meeting comes
	/// back to a warm solution, short enough that several sessions ended over a day do not leave a
	/// gigabyte or more each behind them.
	/// </summary>
	public static readonly TimeSpan LongLivedIdleEviction = TimeSpan.FromMinutes(30);

	/// <summary>
	/// How long a worker may go unused before it is stopped to free its memory, or null to keep every
	/// worker until the broker goes.
	/// <para>
	/// Null by default, because a broker serving one client over stdio ends its workers when that client
	/// ends, which is already the bound. A broker that outlives its clients sets it: there, a warm worker
	/// per solution is the design, and nothing else stops a machine collecting one per solution any
	/// session ever opened. Only tool calls routed to a worker count as use; listing, status and opening
	/// a workspace that is already open do not, so watching one does not keep it warm.
	/// </para>
	/// <para>
	/// Also how long a stopped worker's row stays visible, so a person can see that it was evicted, and
	/// why, before it goes.
	/// </para>
	/// </summary>
	public TimeSpan? IdleEvictionAfter { get; set; }

	/// <summary>
	/// How long a worker's solution file may be missing before the worker is stopped, when eviction is
	/// on. Not immediately, because a branch switch removes and restores a solution file within seconds
	/// and the worker rides that out on its last good snapshot; minutes, because a removed worktree is
	/// not coming back and its worker is memory nobody can use.
	/// </summary>
	public TimeSpan SolutionGoneGrace { get; set; } = TimeSpan.FromMinutes(2);

	/// <summary>
	/// How often the eviction sweep looks, when eviction is on. A minute is far finer than limits
	/// counted in minutes need, and costs a file-existence check per worker.
	/// </summary>
	public TimeSpan EvictionSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

	/// <summary>
	/// The clock idle eviction reads: when a worker started, was last used and stopped, when the sweep
	/// ticks and what it takes "now" to be. The system clock everywhere but a test, which replaces it to
	/// put a sweep at a moment it chooses -- far past the idle limit, inside the instant a hold covers --
	/// rather than waiting for real time to land there, which it practically never does.
	/// </summary>
	public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
