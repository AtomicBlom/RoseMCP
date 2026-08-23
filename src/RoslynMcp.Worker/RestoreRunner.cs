using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

using RoslynMcp.Contracts;

namespace RoslynMcp.Worker;

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
		CancellationToken cancellationToken)
	{
		if (skip)
			return new RestoreReport { Ran = false, Reason = "Skipped: --no-restore was passed." };

		var stale = projectPaths.Where(NeedsRestore).ToArray();
		if (stale.Length == 0)
		{
			return new RestoreReport { Ran = false, Reason = "Not needed: every project has up-to-date restore output." };
		}

		var reason = $"{stale.Length} of {projectPaths.Count} project(s) had missing or stale restore output, "
			+ $"starting with '{Path.GetFileName(stale[0])}'.";
		logger.LogInformation("Running dotnet restore. {Reason}", reason);

		var (exitCode, output) = await RunAsync(solutionPath, cancellationToken);
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
		var assets = Path.Combine(Path.GetDirectoryName(projectPath) ?? ".", "obj", "project.assets.json");
		if (!File.Exists(assets))
			return true;

		var assetsWrittenAt = File.GetLastWriteTimeUtc(assets);
		return RestoreInputs(projectPath).Any(input => File.GetLastWriteTimeUtc(input) > assetsWrittenAt);
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

	private static async Task<(int ExitCode, string Output)> RunAsync(string solutionPath, CancellationToken cancellationToken)
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
