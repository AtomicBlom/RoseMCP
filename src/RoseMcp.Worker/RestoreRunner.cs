using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Decides whether the solution needs a restore, and runs one if so.
/// <para>
/// This matters more than it looks. The design-time build that loads projects reads the real
/// compiler command line, so analyzers and source generators arrive exactly as csc would see them
/// -- but only once restore has produced project.assets.json. Without it the analyzer list is
/// empty and generators silently produce nothing.
/// </para>
/// </summary>
public sealed class RestoreRunner(ILogger<RestoreRunner> logger)
{
	/// <summary>Restore is slow and this runs on the open path, so give it real headroom.</summary>
	private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

	public async Task<RestoreReport> EnsureRestoredAsync(
		string solutionPath,
		IReadOnlyList<string> projectPaths,
		bool skip,
		CancellationToken cancellationToken,
		IWorkProgress? progress = null,
		BuildProperties? build = null)
	{
		if (skip) return new RestoreReport { Ran = false, Reason = "Skipped: --no-restore was passed." };

		var stale = projectPaths.Where(NeedsRestore).ToArray();
		if (stale.Length == 0)
		{
			return new RestoreReport { Ran = false, Reason = "Not needed: every project has up-to-date restore output." };
		}

		var reason = $"{stale.Length} of {projectPaths.Count} project(s) had missing or stale restore output, "
			+ $"starting with '{Path.GetFileName(stale[0])}'.";
		logger.LogInformation("Running dotnet restore. {Reason}", reason);
		progress?.Report("Running dotnet restore");

		var (exitCode, output) = await RunAsync(solutionPath, build, cancellationToken);
		var succeeded = exitCode == 0;

		if (!succeeded)
			logger.LogWarning("dotnet restore failed with exit code {ExitCode}.", exitCode);

		return new RestoreReport
		{
			Ran = true,
			Reason = reason,
			Succeeded = succeeded,
			Output = succeeded ? null : Tail(output),
		};
	}

	/// <summary>
	/// Restore output is stale when the assets file is missing or older than any input that feeds
	/// it. Checking the project alone is not enough -- a Directory.Packages.props edit changes the
	/// resolved graph without touching a single csproj.
	/// </summary>
	private static bool NeedsRestore(string projectPath)
	{
		if (AssetsFiles(projectPath) is not { Length: > 0 } assets) return true;

		// The newest of them, because a project restored under several sets of properties keeps one
		// assets file per set and only the newest describes what a load will find.
		var assetsWrittenAt = assets.Max(File.GetLastWriteTimeUtc);

		return RestoreInputs(projectPath).Any(input => File.GetLastWriteTimeUtc(input) > assetsWrittenAt);
	}

	/// <summary>
	/// Every project.assets.json this project might have, which is not always the one in <c>obj/</c>.
	/// <para>
	/// A repository that builds one source tree against several SDK versions moves
	/// BaseIntermediateOutputPath so their restores stay apart -- <c>obj/2027/</c> for a Revit add-in
	/// built against four Revit APIs -- because otherwise each restore overwrites the last. Looking
	/// only in obj/ reports such a project as unrestored on every load, and then runs a restore that
	/// cannot help.
	/// </para>
	/// </summary>
	private static string[] AssetsFiles(string projectPath)
	{
		var obj = Path.Combine(Path.GetDirectoryName(projectPath) ?? ".", "obj");
		if (!Directory.Exists(obj)) return [];

		var here = Path.Combine(obj, "project.assets.json");
		if (File.Exists(here)) return [here];

		// One level down and no further: below that are the per-configuration build intermediates,
		// which never hold an assets file, and walking them is a directory tree per project per load.
		return [.. Directory.EnumerateDirectories(obj)
			.Select(directory => Path.Combine(directory, "project.assets.json"))
			.Where(File.Exists)];
	}

	private static IEnumerable<string> RestoreInputs(string projectPath)
	{
		yield return projectPath;

		var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
		while (!string.IsNullOrEmpty(directory))
		{
			foreach (var name in (string[])["Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets", "nuget.config", "global.json"])
			{
				var candidate = Path.Combine(directory, name);
				if (File.Exists(candidate))
					yield return candidate;
			}

			directory = Path.GetDirectoryName(directory);
		}
	}

	private static async Task<(int ExitCode, string Output)> RunAsync(
		string solutionPath,
		BuildProperties? build,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = Path.GetDirectoryName(solutionPath),
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		startInfo.ArgumentList.Add("restore");
		startInfo.ArgumentList.Add(solutionPath);

		// The same properties the load will use. A repository that derives its target framework from
		// the configuration also derives where restore writes project.assets.json, so restoring under
		// different properties leaves the assets file somewhere the design-time build will not look.
		foreach (var argument in build?.AsRestoreArguments() ?? [])
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Could not start 'dotnet restore'.");

		var output = new StringBuilder();
		var readStandardOutput = AppendAsync(process.StandardOutput, output);
		var readStandardError = AppendAsync(process.StandardError, output);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(Timeout);

		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}

		await Task.WhenAll(readStandardOutput, readStandardError);
		return (process.ExitCode, output.ToString());
	}

	private static async Task AppendAsync(TextReader reader, StringBuilder sink)
	{
		var text = await reader.ReadToEndAsync();
		lock (sink)
			sink.Append(text);
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already gone. Nothing to do.
		}
	}

	/// <summary>Restore failures are long and front-loaded with noise; the useful part is at the end.</summary>
	private static string Tail(string output, int lines = 30)
	{
		var all = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		return string.Join('\n', all.TakeLast(lines)).Trim();
	}
}
