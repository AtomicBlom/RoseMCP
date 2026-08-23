using System.Runtime.InteropServices;

namespace RoseMcp.Broker;

/// <summary>Locates the worker executable.</summary>
public static class WorkerLauncher
{
	private const string WorkerName = "RoseMcp.Worker";

	public static string ResolveWorkerPath(BrokerOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.WorkerPath))
		{
			if (File.Exists(options.WorkerPath)) return Path.GetFullPath(options.WorkerPath);

			throw new FileNotFoundException($"No worker executable at '{options.WorkerPath}'.", options.WorkerPath);
		}

		var environment = Environment.GetEnvironmentVariable("ROSEMCP_WORKER");
		if (!string.IsNullOrWhiteSpace(environment) && File.Exists(environment)) return Path.GetFullPath(environment);

		var executableName = WorkerName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);

		var alongside = Path.Combine(AppContext.BaseDirectory, executableName);
		if (File.Exists(alongside)) return alongside;

		var inRepository = FindInRepository(executableName);
		if (inRepository is not null) return inRepository;

		throw new FileNotFoundException(
			$"Could not find {executableName}. Publish it alongside the broker, set ROSEMCP_WORKER, "
				+ "or pass --worker with its path.");
	}

	/// <summary>
	/// Development fallback: find the worker in its own build output. Without this the broker only
	/// works from a published layout, which makes running from source needlessly awkward.
	/// </summary>
	private static string? FindInRepository(string executableName)
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		if (directory is null) return null;

		var workerRoot = Path.Combine(directory.FullName, "src", WorkerName, "bin");
		if (!Directory.Exists(workerRoot)) return null;

		return Directory.EnumerateFiles(workerRoot, executableName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}
}
