using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A broker for a test to drive, configured to find nothing unless the test says otherwise.
/// <para>
/// Shared by every broker suite rather than copied, because the default root is the part that has
/// already been got wrong once and would be got wrong again in a copy.
/// </para>
/// <para>
/// <see cref="ProbeTargetSession"/> has a <c>CreateManager</c> of its own, for live-app sessions
/// rather than workspaces. The two are never wanted in one file, and importing both statically would
/// make a bare call ambiguous -- which the compiler says plainly, so this is a note rather than a
/// reason to rename either.
/// </para>
/// </summary>
internal static class BrokerHarness
{
	internal static WorkspaceManager CreateManager(
		string? defaultRoot = null,
		TimeSpan? workerHandshakeTimeout = null)
	{
		var options = Configured(defaultRoot, workerHandshakeTimeout);

		return new(
			options,
			new CallerPaths(options),
			NullLoggerFactory.Instance,
			NullLogger<WorkspaceManager>.Instance);
	}

	/// <summary>
	/// What a tool takes beside the manager to make its path arguments absolute. Rooted the same way,
	/// because a tool measuring a relative path from somewhere its manager does not route from is an
	/// arrangement the registration cannot produce.
	/// </summary>
	internal static CallerPaths CreatePaths(string? defaultRoot = null) => new(Configured(defaultRoot));

	private static IOptions<BrokerOptions> Configured(
		string? defaultRoot,
		TimeSpan? workerHandshakeTimeout = null) => Options.Create(new BrokerOptions
		{
			// Somewhere with no solution, unless a test is specifically exercising discovery. It used
			// to say %TEMP%, which is not that -- a developer's temp collects stray .csproj files, and
			// resolution walks up -- and it went unnoticed because a bare call was answered from the
			// open worker before the root was ever consulted. Now that a bare call really does resolve
			// from here, it has to mean what it says.
			DefaultWorkspaceRoot = defaultRoot ?? NowhereDirectory.Path(),
			WorkerHandshakeTimeout = workerHandshakeTimeout ?? new BrokerOptions().WorkerHandshakeTimeout,
		});
}
