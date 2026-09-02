using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Integration tests for the live-app session foundation. Like the broker tests, these spawn a real
/// host process, because supervision -- starting one, reporting it, reclaiming it -- is a property of
/// process lifetime.
/// </summary>
public sealed class LiveAppSessionTests
{
	/// <summary>
	/// The foundation: the broker launches a host in the target's architecture, the host reports the
	/// session as ready, it shows up in the admin view, and closing it reclaims the process.
	/// </summary>
	[Fact]
	public async Task Starts_a_session_against_a_process_and_reclaims_it()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

		// Attach to this very test process: it is alive and its architecture is the one the host
		// should be launched as.
		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.AttachProcess,
			ProcessId = Environment.ProcessId,
			Description = "integration test target",
		};

		var session = await manager.StartAsync(target, cancellationToken);

		var summary = session.Describe();
		Assert.Equal(LiveAppSessionState.Ready, summary.State);
		Assert.Equal(ExpectedArchitecture, summary.Architecture);
		Assert.Equal(Environment.ProcessId, summary.TargetProcessId);
		Assert.NotNull(summary.HostProcessId);
		Assert.Single(manager.Describe());

		Assert.True(await manager.CloseAsync(session.SessionId, cancellationToken));
		Assert.Empty(manager.Sessions);
	}

	/// <summary>A target that has already gone is reported faulted, not thrown.</summary>
	[Fact]
	public async Task Reports_a_missing_target_as_faulted()
	{
		await using var manager = CreateManager();
		var cancellationToken = TestContext.Current.CancellationToken;

		// A process id that is essentially certain not to exist.
		var target = new LiveAppTarget
		{
			Kind = LiveAppTargetKind.AttachProcess,
			ProcessId = 0x7FFF_FFF0,
			Description = "missing target",
		};

		var session = await manager.StartAsync(target, cancellationToken);

		Assert.Equal(LiveAppSessionState.Faulted, session.Describe().State);
	}

	private static TargetArchitecture ExpectedArchitecture => RuntimeInformation.ProcessArchitecture switch
	{
		System.Runtime.InteropServices.Architecture.X64 => TargetArchitecture.X64,
		System.Runtime.InteropServices.Architecture.Arm64 => TargetArchitecture.Arm64,
		System.Runtime.InteropServices.Architecture.X86 => TargetArchitecture.X86,
		_ => TargetArchitecture.Unknown,
	};

	private static LiveAppSessionManager CreateManager() => new(
		Options.Create(new BrokerOptions()),
		NullLoggerFactory.Instance,
		NullLogger<LiveAppSessionManager>.Instance);
}
