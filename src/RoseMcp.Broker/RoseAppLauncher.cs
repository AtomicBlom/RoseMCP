using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoseMcp.Broker;

/// <summary>
/// Locates the RoseMCP app and builds the command line that points it at this broker: the RoseMCP window, or
/// an Inspector window on a debug session.
/// <para>
/// Here rather than in the tray for one reason: the tray cannot be unit tested, and a path
/// resolution that nothing checks is a path resolution that breaks on the layout nobody ran. The
/// published-layout test stages a tray and the app beside each other and asks this.
/// </para>
/// </summary>
public static class RoseAppLauncher
{
	private const string AppName = "RoseMcp.App";

	/// <summary>What names the app's executable in place of the published layout, for a build run from elsewhere.</summary>
	public const string PathVariable = "ROSEMCP_APP";

	/// <summary>
	/// The app's file name on this operating system. Public for the reason
	/// LiveAppHostLauncher.ExecutableName is: a test that stages one must stage the name this looks
	/// for, or it is testing the extension rather than the layout.
	/// </summary>
	public static string ExecutableName =>
		AppName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);

	/// <summary>The folder a published app goes in, beside the tray's own.</summary>
	private const string PublishedFolder = "app";

	/// <summary>Why the app could not be found, worded for the tray's menu and for an agent that asked for an Inspector.</summary>
	public static string NotInstalled =>
		$"The RoseMCP app is not installed. A published install has it in '{PublishedFolder}' beside the tray's own "
			+ $"folder; from source, build {AppName}, or set {PathVariable} to its executable.";

	/// <summary>
	/// Where the app is, or null when it is not installed.
	/// <para>
	/// Null rather than an exception, because "not installed" is an ordinary state with a sensible
	/// answer: the tray says so on the menu item instead of offering something that cannot work.
	/// A throw would make a missing optional app a failure of the app that is running.
	/// </para>
	/// <para>
	/// Order: an explicit path, then <see cref="PathVariable"/>, then the published layout, then a
	/// build in the repository. Two places count as the published layout because the tray lives in
	/// its own folder: the app is a sibling of that folder, not of the tray's exe.
	/// </para>
	/// </summary>
	public static string? ResolvePath(
		string? configuredPath = null,
		string? baseDirectory = null,
		bool searchRepository = true)
	{
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
		}

		var fromEnvironment = Environment.GetEnvironmentVariable(PathVariable);
		if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
		{
			return Path.GetFullPath(fromEnvironment);
		}

		var executableName = ExecutableName;
		var from = baseDirectory ?? AppContext.BaseDirectory;

		foreach (var root in new[] { Path.Combine(from, ".."), from })
		{
			var published = Path.Combine(root, PublishedFolder, executableName);
			if (File.Exists(published)) return Path.GetFullPath(published);
		}

		return searchRepository ? RepositoryBuildOutput.Find(AppName, executableName, from) : null;
	}

	/// <summary>
	/// The command that starts the app against this broker, for a person to run themselves.
	/// <para>
	/// The exe is quoted because an install path routinely has a space in it, and the token is not,
	/// because base64url has no character a shell would touch.
	/// </para>
	/// </summary>
	public static string CommandLine(
		string appPath,
		string host,
		int port,
		OperatorToken token,
		string? sessionId,
		int? targetProcessId = null)
	{
		var arguments = string.Join(' ', Arguments(host, port, token, sessionId, targetProcessId));

		return $"\"{appPath}\" {arguments}";
	}

	/// <summary>
	/// The arguments the app is started with, as a list rather than a string, so nothing has to
	/// be quoted and nothing can be mis-split.
	/// <para>
	/// A session, a target or <paramref name="inspect"/> asks for an Inspector window; none of them, the
	/// RoseMCP window. The target's process id goes across as well as the session, even though the session
	/// implies it: an Inspector window is one per debugged process, and the app finds the window already open
	/// on that process by it before it has asked the broker anything.
	/// </para>
	/// </summary>
	public static IReadOnlyList<string> Arguments(
		string host,
		int port,
		OperatorToken token,
		string? sessionId,
		int? targetProcessId = null,
		bool inspect = false)
	{
		var arguments = new List<string> { "--host", host, "--port", port.ToString(), "--token", token.Value };

		if (sessionId is { Length: > 0 }) arguments.AddRange(["--session", sessionId]);
		if (targetProcessId is { } pid) arguments.AddRange(["--target-pid", pid.ToString()]);
		if (inspect) arguments.Add("--inspect");

		return arguments;
	}

	/// <summary>
	/// Starts the app, pointed at this broker: the RoseMCP window, or an Inspector window on one session, or,
	/// with <paramref name="inspect"/>, an Inspector window that settles on a session itself.
	/// <para>
	/// <c>UseShellExecute</c> is off so the arguments go across as a list: a token on a command line
	/// the shell re-parses is a token that can come back different.
	/// </para>
	/// <para>
	/// Starting a second one while one is open is not a problem to guard against here. The app keys its
	/// single instance on this broker, so the second launch hands its arguments to the app that is open and
	/// exits -- which opens or brings forward the window asked for, and hands over this run's token, which is
	/// how a window left open across a broker restart is reconnected.
	/// </para>
	/// </summary>
	/// <exception cref="FileNotFoundException">The app is not installed.</exception>
	public static Process Launch(
		string host,
		int port,
		OperatorToken token,
		string? sessionId = null,
		string? configuredPath = null,
		int? targetProcessId = null,
		bool inspect = false)
	{
		var path = ResolvePath(configuredPath) ?? throw new FileNotFoundException(NotInstalled);

		var start = new ProcessStartInfo(path) { UseShellExecute = false };

		foreach (var argument in Arguments(host, port, token, sessionId, targetProcessId, inspect))
		{
			start.ArgumentList.Add(argument);
		}

		return Process.Start(start)
			?? throw new FileNotFoundException($"Windows did not start {path}.");
	}
}
