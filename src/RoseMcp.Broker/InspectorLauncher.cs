using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoseMcp.Broker;

/// <summary>
/// Locates the inspector executable and builds the command line that points it at this broker.
/// <para>
/// Here rather than in the tray for one reason: the tray cannot be unit tested, and a path
/// resolution that nothing checks is a path resolution that breaks on the layout nobody ran. The
/// published-layout test stages a tray and an inspector beside each other and asks this.
/// </para>
/// </summary>
public static class InspectorLauncher
{
	private const string InspectorName = "RoseMcp.Inspector";

	/// <summary>
	/// The inspectors file name on this operating system. Public for the reason
	/// LiveAppHostLauncher.ExecutableName is: a test that stages one must stage the name this looks
	/// for, or it is testing the extension rather than the layout.
	/// </summary>
	public static string ExecutableName =>
		InspectorName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);

	/// <summary>The folder a published inspector goes in, beside the tray's own.</summary>
	private const string PublishedFolder = "inspector";

	/// <summary>
	/// Where the inspector is, or null when it is not installed.
	/// <para>
	/// Null rather than an exception, because "not installed" is an ordinary state with a sensible
	/// answer: the tray says so on the menu item instead of offering something that cannot work.
	/// A throw would make a missing optional app a failure of the app that is running.
	/// </para>
	/// <para>
	/// Order: an explicit path, then <c>ROSEMCP_INSPECTOR</c>, then the published layout, then a
	/// build in the repository. Two places count as the published layout because the tray lives in
	/// its own folder: the inspector is a sibling of that folder, not of the tray's exe.
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

		var fromEnvironment = Environment.GetEnvironmentVariable("ROSEMCP_INSPECTOR");
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

		return searchRepository ? RepositoryBuildOutput.Find(InspectorName, executableName, from) : null;
	}

	/// <summary>
	/// The command that starts the inspector against this broker, for a person to run themselves.
	/// <para>
	/// The exe is quoted because an install path routinely has a space in it, and the token is not,
	/// because base64url has no character a shell would touch.
	/// </para>
	/// </summary>
	public static string CommandLine(
		string inspectorPath,
		string host,
		int port,
		OperatorToken token,
		string? sessionId,
		int? targetProcessId = null)
	{
		var arguments = string.Join(' ', Arguments(host, port, token, sessionId, targetProcessId));

		return $"\"{inspectorPath}\" {arguments}";
	}

	/// <summary>
	/// The arguments the inspector is started with, as a list rather than a string, so nothing has to
	/// be quoted and nothing can be mis-split.
	/// <para>
	/// The target's process id goes across as well as the session, even though the session implies it.
	/// The inspector is one window per debugged process and claims its single-instance key on that
	/// before it has spoken to the broker, and a key it would have to ask for is a key it cannot claim
	/// in time -- by then a second window exists.
	/// </para>
	/// </summary>
	public static IReadOnlyList<string> Arguments(
		string host,
		int port,
		OperatorToken token,
		string? sessionId,
		int? targetProcessId = null)
	{
		var arguments = new List<string> { "--host", host, "--port", port.ToString(), "--token", token.Value };

		if (sessionId is { Length: > 0 }) arguments.AddRange(["--session", sessionId]);
		if (targetProcessId is { } pid) arguments.AddRange(["--target-pid", pid.ToString()]);

		return arguments;
	}

	/// <summary>
	/// Starts the inspector, pointed at this broker and optionally at one session.
	/// <para>
	/// <c>UseShellExecute</c> is off so the arguments go across as a list: a token on a command line
	/// the shell re-parses is a token that can come back different.
	/// </para>
	/// <para>
	/// Starting a second one for a process that already has an inspector is not a problem to guard
	/// against here. The inspector keys its single instance on the target, so the second launch
	/// hands its arguments to the window that is open and exits -- which is what makes Inspect mean
	/// "show me this" rather than "open another one of these".
	/// </para>
	/// </summary>
	/// <exception cref="FileNotFoundException">The inspector is not installed.</exception>
	public static Process Launch(
		string host,
		int port,
		OperatorToken token,
		string? sessionId = null,
		string? configuredPath = null,
		int? targetProcessId = null)
	{
		var path = ResolvePath(configuredPath)
			?? throw new FileNotFoundException(
				$"Could not find {InspectorName}. A published install has it in '{PublishedFolder}' beside the "
					+ "tray's own folder; from source, build it, or set ROSEMCP_INSPECTOR to its executable.");

		var start = new ProcessStartInfo(path) { UseShellExecute = false };

		foreach (var argument in Arguments(host, port, token, sessionId, targetProcessId))
		{
			start.ArgumentList.Add(argument);
		}

		return Process.Start(start)
			?? throw new FileNotFoundException($"Windows did not start {path}.");
	}
}
