using System.Runtime.InteropServices;

namespace RoseMcp.Broker;

/// <summary>Locates the worker executable.</summary>
public static class WorkerLauncher
{
	private const string WorkerName = "RoseMcp.Worker";

	public static string ResolveWorkerPath(
		BrokerOptions options,
		string? baseDirectory = null,
		bool searchRepository = true)
	{
		if (!string.IsNullOrWhiteSpace(options.WorkerPath))
		{
			if (File.Exists(options.WorkerPath)) return Path.GetFullPath(options.WorkerPath);

			throw new FileNotFoundException($"No worker executable at '{options.WorkerPath}'.", options.WorkerPath);
		}

		var environment = Environment.GetEnvironmentVariable("ROSEMCP_WORKER");
		if (!string.IsNullOrWhiteSpace(environment) && File.Exists(environment)) return Path.GetFullPath(environment);

		var executableName = WorkerName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);

		// Two places count as "beside", the same two the live-app host looks in and for the same
		// reason: the server publishes flat into the install root while the tray goes in a tray/
		// subfolder, so what is beside one is one level up from the other. Looking only alongside left
		// the tray unable to start a worker at all without --worker (#101), which is invisible from the
		// repository because the development fallback below finds it anyway.
		var root = baseDirectory ?? AppContext.BaseDirectory;

		foreach (var directory in new[] { root, Path.Combine(root, "..") })
		{
			var alongside = Path.Combine(directory, executableName);
			if (File.Exists(alongside)) return Path.GetFullPath(alongside);
		}

		var inRepository = searchRepository ? RepositoryBuildOutput.Find(WorkerName, executableName, root) : null;
		if (inRepository is not null) return inRepository;

		throw new FileNotFoundException(
			$"Could not find {executableName}. Publish it alongside the broker, set ROSEMCP_WORKER, "
				+ "or pass --worker with its path.");
	}
}
