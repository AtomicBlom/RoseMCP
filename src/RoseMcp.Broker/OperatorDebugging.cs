using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RoseMcp.Contracts;
using RoseMcp.Settings;

namespace RoseMcp.Broker;

/// <summary>
/// What a person debugging without an agent asks the broker through the operator API: which apps are running and
/// which are installed, what they debugged lately, attaching and launching, and sharing a session with one agent
/// session.
/// <para>
/// Starting goes through <see cref="LiveAppSessionManager"/>'s attach and launch, the same ones the agent tools
/// call, so a person's start keeps every rule an agent's does and is reclaimed the same way when it fails. What differs
/// is only who the session belongs to -- the person, whom no MCP call is -- and that a launch names its program in
/// full, since a window has no folder to measure a relative path from.
/// </para>
/// </summary>
public sealed class OperatorDebugging(
	LiveAppSessionManager sessions,
	ConnectedClients clients,
	IRunningApps running,
	IInstalledApps installed,
	OperatorViews views,
	IOptions<BrokerOptions> options,
	ILogger<OperatorDebugging> logger)
{
	private readonly BrokerOptions _options = options.Value;

	/// <summary>The longest display name a request may give a packaged app, which a window shows and a file keeps.</summary>
	private const int MaxDisplayName = 200;

	/// <summary>
	/// The running .NET apps with a window, or every .NET process with <paramref name="every"/>, each naming the open
	/// session debugging it where there is one.
	/// </summary>
	public RunningAppList Apps(bool every)
	{
		var open = sessions.Sessions
			.Select(session => (Session: session, Summary: session.Describe()))
			.Where(pair => pair.Session.IsAlive && pair.Summary.State is not (LiveAppSessionState.Ended or LiveAppSessionState.Faulted))
			.Where(pair => pair.Summary.TargetProcessId is not null)
			.GroupBy(pair => pair.Summary.TargetProcessId!.Value)
			.ToDictionary(group => group.Key, group => group.First().Summary);

		var apps = running.Read(every).Select(app => open.TryGetValue(app.ProcessId, out var session)
			? app with
			{
				SessionId = session.SessionId,
				SessionState = session.State,
				SessionStartedBy = session.StartedBy,
				SessionStopped = session.Execution != LiveExecutionState.Running,
			}
			: app);

		return new RunningAppList
		{
			Apps = [.. apps],
			Every = every,
			Detail = OperatingSystem.IsWindows() ? null : "Running apps are read from Windows, and this broker is not running on it.",
		};
	}

	/// <summary>The packaged apps installed for this user, those in development mode first.</summary>
	public InstalledAppList Packages() => new()
	{
		Apps = installed.Read(),
		Detail = OperatingSystem.IsWindows() ? null : "Packaged apps are read from Windows, and this broker is not running on it.",
	};

	/// <summary>What the person debugged from a window lately, newest first.</summary>
	public RecentTargetList Recent() => new()
	{
		Targets =
		[
			.. RecentTargetsFile.Read(_options.LocalAppData)
				.Select(record => Enum.TryParse<LiveAppTargetKind>(record.Kind, out var kind)
					? new RecentTarget
					{
						Kind = kind,
						DisplayName = record.DisplayName,
						ExecutablePath = record.ExecutablePath,
						Arguments = record.Arguments,
						AppUserModelId = record.AppUserModelId,
						LastUsedUtc = record.LastUsedUtc,
					}
					: null)
				.OfType<RecentTarget>(),
		],
	};

	/// <summary>
	/// Attaches to a running process for the person: the session debugging it, started now or already open, since a
	/// process has one debugger.
	/// </summary>
	/// <exception cref="ArgumentException">The process may not be attached to.</exception>
	/// <exception cref="LiveAppRefusedException">The host could not attach, or an attach to it is already starting.</exception>
	public async Task<DebugStart> AttachAsync(AttachRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var started = await sessions.AttachAsync(request.ProcessId, LiveAppOwners.Person, cancellationToken);

		if (!started.Existing)
		{
			var executable = ExecutableOf(request.ProcessId);

			Remember(new RecentTargetRecord
			{
				Kind = nameof(LiveAppTargetKind.AttachProcess),
				DisplayName = executable is null ? started.Session.Target.Description ?? $"pid {request.ProcessId}" : Path.GetFileNameWithoutExtension(executable),
				ExecutablePath = executable,
				LastUsedUtc = sessions.UtcNow,
			});
		}

		return Answer(started);
	}

	/// <summary>Launches a program, by its full path, or a packaged app, for the person.</summary>
	/// <exception cref="ArgumentException">The request names neither or both, or what it names cannot be launched.</exception>
	/// <exception cref="LiveAppRefusedException">The host could not launch it.</exception>
	public async Task<DebugStart> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var program = !string.IsNullOrWhiteSpace(request.ExecutablePath);
		var package = !string.IsNullOrWhiteSpace(request.AppUserModelId);

		if (program == package)
		{
			throw new ArgumentException(
				"A launch names one thing to run: a program, by executablePath with any arguments, or a packaged app, by "
					+ "appUserModelId.");
		}

		if (program)
		{
			var started = await sessions.LaunchAsync(request.ExecutablePath!, request.Arguments, LiveAppOwners.Person, cancellationToken);
			var path = started.Session.Target.ExecutablePath ?? request.ExecutablePath!;

			Remember(new RecentTargetRecord
			{
				Kind = nameof(LiveAppTargetKind.LaunchExecutable),
				DisplayName = Path.GetFileName(path),
				ExecutablePath = path,
				Arguments = started.Session.Target.Arguments,
				LastUsedUtc = sessions.UtcNow,
			});

			return Answer(started);
		}

		var named = ConnectedClients.Clean(request.DisplayName, MaxDisplayName);
		var activated = await sessions.LaunchPackageAsync(request.AppUserModelId!, named, LiveAppOwners.Person, cancellationToken);

		if (!activated.Existing)
		{
			var aumid = activated.Session.Target.AppUserModelId ?? request.AppUserModelId!;

			Remember(new RecentTargetRecord
			{
				Kind = nameof(LiveAppTargetKind.LaunchUwp),
				DisplayName = named ?? aumid,
				AppUserModelId = aumid,
				LastUsedUtc = sessions.UtcNow,
			});
		}

		return Answer(activated);
	}

	/// <summary>
	/// Shares a session the person started with the agent session <paramref name="request"/> names, in place of any it
	/// was shared with.
	/// </summary>
	/// <exception cref="KeyNotFoundException">No such session is open, or the client table lists no such client.</exception>
	/// <exception cref="ArgumentException">The client is the one unnamed row, which could be any client without a relay.</exception>
	/// <exception cref="LiveAppRefusedException">An agent started the session.</exception>
	public async Task<LiveAppSessionSummary> ShareAsync(string sessionId, ShareRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var clientId = request.Client?.Trim();
		if (string.IsNullOrEmpty(clientId))
		{
			throw new ArgumentException("A share names the client it is for, by the id GET /operator/clients lists it under.");
		}

		var (key, row) = clients.ByKey().FirstOrDefault(pair => string.Equals(pair.Value.Id, clientId, StringComparison.Ordinal));
		if (key is null || row is null)
		{
			throw new KeyNotFoundException(
				$"The broker lists no client {clientId}. GET /operator/clients lists the ones there are; a session's row goes "
					+ "a couple of minutes after its relay lets go of the broker.");
		}

		var isUnnamed = key == ConnectedClients.KeyOf(relayed: null, transportSession: null);
		if (isUnnamed)
		{
			throw new ArgumentException(
				"That row stands for every client that reached the broker over http with neither the relay nor a session of "
					+ "its own, so sharing with it would hand the debugger to whichever of them called. Register Rose with the "
					+ "agent through the relay to share a session with it.");
		}

		var shared = await sessions.ShareAsync(sessionId, new LiveAppSharedClient(key, row.Id, OperatorViews.ClientName(row)), cancellationToken)
			?? throw Gone(sessionId);

		return views.Session(shared);
	}

	/// <summary>Stops sharing a session with the agent session it was shared with, if it was.</summary>
	/// <exception cref="KeyNotFoundException">No such session is open.</exception>
	public async Task<LiveAppSessionSummary> UnshareAsync(string sessionId, CancellationToken cancellationToken)
	{
		var unshared = await sessions.UnshareAsync(sessionId, cancellationToken) ?? throw Gone(sessionId);

		return views.Session(unshared);
	}

	private DebugStart Answer(LiveAppStart started) => new() { Session = views.Session(started.Session), Existing = started.Existing };

	private static KeyNotFoundException Gone(string sessionId) =>
		new($"Debug session '{sessionId}' ended while it was being changed. GET /operator/sessions lists the ones there are.");

	/// <summary>Records a target as the newest the person debugged, saying so in the log where the file would not take it.</summary>
	private void Remember(RecentTargetRecord target)
	{
		if (!RecentTargetsFile.Record(target, _options.LocalAppData))
		{
			logger.LogWarning("Could not record {Target} in {File}; it is debugged all the same.", target.DisplayName, RecentTargetsFile.PathFor(_options.LocalAppData));
		}
	}

	/// <summary>The executable a process runs, where it can be read.</summary>
	private static string? ExecutableOf(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return process.MainModule?.FileName;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
