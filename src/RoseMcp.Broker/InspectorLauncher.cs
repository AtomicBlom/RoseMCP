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

		var executableName = InspectorName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);
		var from = baseDirectory ?? AppContext.BaseDirectory;

		foreach (var root in new[] { Path.Combine(from, ".."), from })
		{
			var published = Path.Combine(root, PublishedFolder, executableName);
			if (File.Exists(published)) return Path.GetFullPath(published);
		}

		return searchRepository ? FindInRepository(executableName, from) : null;
	}

	/// <summary>
	/// The command that starts the inspector against this broker, for a person to run themselves.
	/// <para>
	/// The exe is quoted because an install path routinely has a space in it, and the token is not,
	/// because base64url has no character a shell would touch.
	/// </para>
	/// </summary>
	public static string CommandLine(string inspectorPath, string host, int port, OperatorToken token, string? sessionId)
	{
		var command = $"\"{inspectorPath}\" --host {host} --port {port} --token {token.Value}";

		return sessionId is { Length: > 0 } ? $"{command} --session {sessionId}" : command;
	}

	/// <summary>
	/// The arguments the inspector is started with, as a list rather than a string, so nothing has to
	/// be quoted and nothing can be mis-split.
	/// </summary>
	public static IReadOnlyList<string> Arguments(string host, int port, OperatorToken token, string? sessionId)
	{
		var arguments = new List<string> { "--host", host, "--port", port.ToString(), "--token", token.Value };

		if (sessionId is { Length: > 0 }) arguments.AddRange(["--session", sessionId]);

		return arguments;
	}

	/// <summary>
	/// Starts the inspector, pointed at this broker and optionally at one session.
	/// <para>
	/// <c>UseShellExecute</c> is off so the arguments go across as a list: a token on a command line
	/// the shell re-parses is a token that can come back different.
	/// </para>
	/// </summary>
	/// <exception cref="FileNotFoundException">The inspector is not installed.</exception>
	public static Process Launch(
		string host,
		int port,
		OperatorToken token,
		string? sessionId = null,
		string? configuredPath = null)
	{
		var path = ResolvePath(configuredPath)
			?? throw new FileNotFoundException(
				$"Could not find {InspectorName}. A published install has it in '{PublishedFolder}' beside the "
					+ "tray's own folder; from source, build it, or set ROSEMCP_INSPECTOR to its executable.");

		var start = new ProcessStartInfo(path) { UseShellExecute = false };

		foreach (var argument in Arguments(host, port, token, sessionId))
		{
			start.ArgumentList.Add(argument);
		}

		return Process.Start(start)
			?? throw new FileNotFoundException($"Windows did not start {path}.");
	}

	/// <summary>
	/// Development fallback: the inspector's own build output, narrowed to the running app's
	/// configuration first.
	/// <para>
	/// Configuration before recency, for the reason the live-app host resolver records: a Release
	/// artefact left by a deploy would otherwise shadow a Debug build twenty minutes newer, and a
	/// Debug run would silently launch a binary without the change under test.
	/// </para>
	/// </summary>
	private static string? FindInRepository(string executableName, string from)
	{
		var directory = new DirectoryInfo(from);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		if (directory is null) return null;

		var root = Path.Combine(directory.FullName, "src", InspectorName, "bin");
		if (!Directory.Exists(root)) return null;

		var candidates = Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.ToList();

		var configuration = ConfigurationOf(from);
		if (configuration is not null)
		{
			var matching = candidates
				.Where(path => path.Contains(
					$"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}",
					StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (matching.Count > 0) candidates = matching;
		}

		return candidates.FirstOrDefault();
	}

	/// <summary>
	/// Which build configuration a directory belongs to, or null when it is not a build output at all
	/// (a published layout, where the question does not arise).
	/// </summary>
	private static string? ConfigurationOf(string directory)
	{
		var separator = Path.DirectorySeparatorChar;

		foreach (var configuration in new[] { "Debug", "Release" })
		{
			if (directory.Contains($"{separator}{configuration}{separator}", StringComparison.OrdinalIgnoreCase))
			{
				return configuration;
			}
		}

		return null;
	}
}
