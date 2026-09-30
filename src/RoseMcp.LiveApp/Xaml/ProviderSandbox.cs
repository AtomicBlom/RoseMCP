using System.ComponentModel;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// The folder a session stages the XAML provider into, and its whole life: clearing out what dead
/// hosts left, copying the DLL somewhere the target can reach, granting the app container access to
/// it, and taking it away again.
/// <para>
/// Its own object because the folder outlives nothing else here and nothing else here touches it.
/// The session's other state is the pipe and the tap, which are about talking to a provider that is
/// already loaded; this is about there being a file to load at all.
/// </para>
/// <para>
/// Staged once per session and reused, because the first injection loads the DLL into the target,
/// which holds the file open -- so a later injection could not overwrite it and has no reason to,
/// it being the same provider.
/// </para>
/// </summary>
internal sealed class ProviderSandbox(ILogger logger)
{
	private string? _workDir;
	private string? _stagedProvider;

	/// <summary>Whether anything has been staged, which is whether there is anything to take away.</summary>
	internal bool Staged => _workDir is not null;

	/// <summary>
	/// The staged provider, copied and granted if it is not there already, and whether this call is
	/// what created it -- which is what tells a caller its pipe has to be made too.
	/// </summary>
	internal (string WorkDir, string StagedProvider, bool Fresh) Stage(XamlTap tap, string provider, XamlChannelBounds bounds)
	{
		// Stage once per session and reuse: the first injection loads the provider DLL into the target,
		// which holds the file open, so a later injection cannot overwrite it -- and need not, since it
		// is the same provider. Each request re-injects from this one staged copy.
		if (_workDir is not null && _stagedProvider is not null && File.Exists(_stagedProvider))
		{
			return (_workDir, _stagedProvider, false);
		}

		var root = Path.Combine(Path.GetTempPath(), "RoseMcpXaml");

		// Before staging anything, clear out what earlier hosts left behind. Each folder holds a copy of
		// the provider and a grant to ALL APPLICATION PACKAGES, so one that outlives its host is a
		// world-readable directory accumulating in the user's temp directory.
		Sweep(root);

		var workDir = Path.Combine(root, Environment.ProcessId.ToString());

		// Our own pid's folder goes too, because a pid is reusable. A host that draws a recycled pid
		// used to find a populated folder and, worse than a stale state file, load a stale *provider*:
		// the copy below was skipped whenever the DLL was already there, so deploying a new provider
		// and getting the old one was silent and every symptom pointed at the change just made. It is
		// also why the fast rebuild loop (build the provider, copy it over, restart the app) worked at
		// all -- a new pid meant a fresh copy -- and it would have stopped working the first time a
		// pid came round again.
		TryDeleteDirectory(workDir);
		Directory.CreateDirectory(workDir);

		// Unconditional. The overwrite was always there and always unreachable behind the existence
		// test; it can only be reached now because the folder above is cleared first, which is why
		// the two halves of this fix have to land together. One file copy per session is nothing
		// beside injecting into a process.
		var stagedProvider = Path.Combine(workDir, tap.ProviderFileName);
		File.Copy(provider, stagedProvider, overwrite: true);

		// ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2):
		// Modify grants read+execute to load the DLL and read commands, and write for the provider's
		// snapshot and log. Without this the sandboxed provider cannot touch the folder at all.
		//
		// Asked of the tap rather than done always (#74). An unpackaged WinUI 3 app is not in an
		// AppContainer and needs none of it, and granting anyway would leave a world-readable
		// directory in TEMP for every session, for nothing.
		if (tap.NeedsAppContainerGrants)
		{
			foreach (var sid in new[] { "*S-1-15-2-1", "*S-1-15-2-2" })
			{
				Icacls(workDir, $"/grant {sid}:(OI)(CI)(M)", bounds);
			}
		}

		_workDir = workDir;
		_stagedProvider = stagedProvider;
		return (workDir, stagedProvider, true);
	}

	/// <summary>
	/// Deletes the sandbox folders whose host is gone. <see cref="SandboxSweep"/> decides each one, and
	/// says how a recycled pid is told from a live host and why a folder that cannot be decided costs
	/// only that folder.
	/// </summary>
	private void Sweep(string root)
	{
		List<string> folders;
		try
		{
			if (!Directory.Exists(root)) return;

			folders = [.. Directory.EnumerateDirectories(root)];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Tidying, never the job: a root that cannot be listed costs disk and nothing else, and
			// failing a XAML call over it would be the wrong trade entirely.
			logger.LogDebug(exception, "Could not list the XAML provider sandbox folders under {Root}.", root);
			return;
		}

		var plan = SandboxSweep.Plan(folders, Environment.ProcessId, Directory.GetCreationTimeUtc, Holder);

		var removed = 0;
		foreach (var folder in plan.Stale)
		{
			if (TryDeleteDirectory(folder)) removed++;
		}

		// Counted rather than merely attempted, because a sweep that removes nothing and a sweep with
		// nothing to remove are otherwise the same line in the log -- and one that fails on every run
		// looks exactly like the second.
		var nothingToReport = plan.Stale.Count == 0 && plan.Undecided == 0;
		logger.Log(
			nothingToReport ? LogLevel.Debug : LogLevel.Information,
			"Swept the XAML provider sandbox folders under {Root}: removed {Removed}, {Held} still held open by an "
				+ "app with the provider loaded, {Running} kept for a running host, {Undecided} kept because their "
				+ "host could not be asked about.",
			root,
			removed,
			plan.Stale.Count - removed,
			plan.Running,
			plan.Undecided);
	}

	/// <summary>ERROR_ACCESS_DENIED, which is how a process that is not this user's refuses to be asked.</summary>
	private const int AccessDenied = 5;

	/// <summary>
	/// What holds <paramref name="pid"/>, asked for its start time and nothing more: the start time is
	/// what tells a recycled id from the host that made the folder, and a process that will not answer
	/// even that is not this user's.
	/// </summary>
	private static SandboxProcess Holder(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return SandboxProcess.StartedAt(process.StartTime);
		}
		catch (ArgumentException)
		{
			return SandboxProcess.None;
		}
		catch (InvalidOperationException)
		{
			// It exited between being found and being asked.
			return SandboxProcess.None;
		}
		catch (Win32Exception exception) when (exception.NativeErrorCode == AccessDenied)
		{
			return SandboxProcess.Inaccessible;
		}
	}

	private static bool TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
			return true;
		}
		catch (Exception)
		{
			// Whatever is still held belongs to an app that has the provider loaded, and that app
			// outlives the debug session on purpose -- detaching leaves it running. A later sweep takes
			// the folder once nothing holds it, which is why the sweep and this go together.
			return false;
		}
	}

	private void Icacls(string path, string arguments, XamlChannelBounds bounds)
	{
		try
		{
			var start = new ProcessStartInfo("icacls.exe", $"\"{path}\" {arguments}")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using var process = Process.Start(start);
			if (process is null) return;

			// Bounded like every other wait on this path. A grant that never finishes is a tool call
			// that never returns, and the AppContainer grants are the last thing between staging the
			// provider and injecting it -- so a wait with no bound here hangs exactly where the pipe
			// has just been logged as listening.
			if (process.WaitForExit((int)bounds.Grant.TotalMilliseconds)) return;

			logger.LogWarning(
				"icacls {Arguments} on {Path} did not finish within {Seconds}s; the provider may not be able "
					+ "to reach the work folder.",
				arguments,
				path,
				bounds.Grant.TotalSeconds);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "icacls {Arguments} on {Path} failed.", arguments, path);
		}
	}

	/// <summary>
	/// Deletes this session's folder. Best effort by nature: the staged provider is loaded into an
	/// app meant to still be running afterwards, so the DLL is held open and only the next host's
	/// sweep can finish the job.
	/// </summary>
	internal void Discard()
	{
		if (_workDir is null) return;

		TryDeleteDirectory(_workDir);
		_workDir = null;
		_stagedProvider = null;
	}
}
