using System.Globalization;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// What an Inspector window opened on its own shows before it is about a process: the wording, the launch form's
/// rules, the recent targets as chips, and who a session can be shared with. Free of WinUI, so a test reads it.
/// </summary>
public static class StartPage
{
	/// <summary>The sentence under the running apps, saying what the list holds.</summary>
	public static string AppsCaption(bool every, int count) => every
		? $"Every .NET process you run: {Format.Count(count, "process", "processes")}."
		: $"The .NET apps with a window: {Format.Count(count, "app")}.";

	/// <summary>What the start page says about sharing, which is the one thing about a session a person starts that is not obvious.</summary>
	public const string Sharing =
		"A session you start is yours. An agent sees it only after you share it with that agent's session, from the "
			+ "session's header, and you can stop sharing whenever you like.";

	/// <summary>
	/// A program to launch, from what the form holds: its path in full, quotes around it dropped, and its arguments as
	/// typed. Null with a reason where the path is not one the broker will take, so the form says so before asking.
	/// </summary>
	public static LaunchRequest? Program(string? path, string? arguments, out string? problem)
	{
		var program = path?.Trim().Trim('"').Trim() ?? string.Empty;
		problem = null;

		if (program.Length == 0)
		{
			problem = "Name the program to launch: the full path to its .exe.";
			return null;
		}

		var isRooted = Path.IsPathFullyQualified(program) || IsWindowsFullPath(program);
		if (!isRooted)
		{
			problem = $"'{program}' is not a full path. Give the program's path from its drive, such as D:\\Tools\\App.exe.";
			return null;
		}

		var isExecutable = program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
		if (!isExecutable)
		{
			problem = $"{program} is not an .exe. The debugger launches a program's executable.";
			return null;
		}

		return new LaunchRequest
		{
			ExecutablePath = program,
			Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim(),
		};
	}

	/// <summary>A packaged app to launch, named as the installed list names it.</summary>
	public static LaunchRequest Package(InstalledApp app)
	{
		ArgumentNullException.ThrowIfNull(app);

		return new LaunchRequest { AppUserModelId = app.AppUserModelId, DisplayName = app.DisplayName };
	}

	/// <summary>What an installed app's row in the launch list says: its name and its package, development mode marked.</summary>
	public static string Describe(InstalledApp app)
	{
		ArgumentNullException.ThrowIfNull(app);

		var development = app.DevelopmentMode ? " · development" : string.Empty;

		return $"{app.DisplayName} · {app.PackageFamilyName}{development}";
	}

	/// <summary>
	/// What a recent target's chip says: its name, what was done, and when -- today, yesterday, so many days ago, or
	/// the date.
	/// </summary>
	public static string Chip(RecentTarget target, DateTime nowUtc)
	{
		ArgumentNullException.ThrowIfNull(target);

		var done = target.Kind == LiveAppTargetKind.AttachProcess ? "attached" : "launched";

		return $"{target.DisplayName} · {done} · {When(target.LastUsedUtc, nowUtc)}";
	}

	/// <summary>When something was last done, in days as a person counts them in their own time zone.</summary>
	public static string When(DateTime thenUtc, DateTime nowUtc)
	{
		var then = DateTime.SpecifyKind(thenUtc, DateTimeKind.Utc).ToLocalTime().Date;
		var today = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToLocalTime().Date;
		var days = (today - then).Days;

		return days switch
		{
			<= 0 => "today",
			1 => "yesterday",
			< 7 => $"{days} days ago",
			_ => then.ToString("d MMM", CultureInfo.InvariantCulture),
		};
	}

	/// <summary>
	/// What doing a recent target again is: launching it as it was launched, or attaching to the running app with its
	/// executable -- the one, since attaching to another instance of the same program is a guess. Null with a reason
	/// where an attach finds none or several running.
	/// </summary>
	public static RecentAction? Again(RecentTarget target, IReadOnlyList<RunningApp> running, out string? problem)
	{
		ArgumentNullException.ThrowIfNull(target);
		problem = null;

		switch (target.Kind)
		{
			case LiveAppTargetKind.LaunchExecutable when target.ExecutablePath is { Length: > 0 } path:
				return new RecentAction(new LaunchRequest { ExecutablePath = path, Arguments = target.Arguments }, ProcessId: null);

			case LiveAppTargetKind.LaunchUwp when target.AppUserModelId is { Length: > 0 } aumid:
				return new RecentAction(new LaunchRequest { AppUserModelId = aumid, DisplayName = target.DisplayName }, ProcessId: null);

			case LiveAppTargetKind.AttachProcess:
				var matching = running
					.Where(app => target.ExecutablePath is { Length: > 0 } executable
						&& string.Equals(app.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
					.ToList();

				if (matching.Count == 1) return new RecentAction(Launch: null, matching[0].ProcessId);

				problem = matching.Count == 0
					? $"{target.DisplayName} is not running, so there is nothing to attach to. Start it, then attach from the list."
					: $"{matching.Count} copies of {target.DisplayName} are running; attach to the one you mean from the list.";
				return null;

			default:
				problem = $"{target.DisplayName} was recorded without what it would take to debug it again.";
				return null;
		}
	}

	/// <summary>
	/// The agent sessions a person can share a session with: those behind a relay, in the order the agent cards are,
	/// each named as a note's recipient is. The unnamed client is never one, since it could be any client without a relay.
	/// </summary>
	public static IReadOnlyList<NoteRecipient> ShareChoices(IReadOnlyList<ClientSummary> clients)
	{
		var named = clients.Where(client => client.Named).ToList();

		return [.. AgentCard.Order(named).Select(client => NoteTargets.Recipient(client, named))];
	}

	/// <summary>A path written for Windows, drive and all, which a test on another platform reads the same as Windows does.</summary>
	private static bool IsWindowsFullPath(string path) =>
		path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
}

/// <summary>What doing a recent target again asks the broker for: a launch, or an attach to one running process.</summary>
/// <param name="Launch">The launch to make, or null for an attach.</param>
/// <param name="ProcessId">The process to attach to, or null for a launch.</param>
public sealed record RecentAction(LaunchRequest? Launch, int? ProcessId);

/// <summary>One recent target as the start page shows it: a chip saying what it was and when, which does it again.</summary>
/// <param name="Target">What was debugged.</param>
/// <param name="Label">What the chip says (<see cref="StartPage.Chip"/>).</param>
public sealed record RecentTargetChip(RecentTarget Target, string Label);
