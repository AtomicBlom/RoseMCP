using RoseMcp.Contracts;

namespace RoseMcp.LiveApp;

/// <summary>Command line for a live-app host. One host owns exactly one target.</summary>
public sealed class LiveAppOptions
{
	public required LiveAppTarget Target { get; init; }

	/// <summary>A short token for the log file name; derived from the target.</summary>
	public string LogDiscriminator => Target switch
	{
		{ ProcessId: { } pid } => $"pid{pid}",
		{ AppUserModelId: { } aumid } => aumid,
		{ ExecutablePath: { } path } => Path.GetFileNameWithoutExtension(path),
		_ => "session",
	};

	public static LiveAppOptions Parse(string[] args)
	{
		int? attachPid = null;
		string? launchPath = null;
		string? launchUwp = null;
		string? arguments = null;
		string? description = null;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--attach":
					if (i + 1 >= args.Length || !int.TryParse(args[++i], out var pid))
					{
						throw new ArgumentException("--attach requires a process id.");
					}

					attachPid = pid;
					break;

				case "--launch":
					if (i + 1 >= args.Length) throw new ArgumentException("--launch requires an executable path.");
					launchPath = args[++i];
					break;

				case "--launch-uwp":
					if (i + 1 >= args.Length) throw new ArgumentException("--launch-uwp requires an app user-model id.");
					launchUwp = args[++i];
					break;

				case "--arguments":
					if (i + 1 >= args.Length) throw new ArgumentException("--arguments requires a value.");
					arguments = args[++i];
					break;

				case "--description":
					if (i + 1 >= args.Length) throw new ArgumentException("--description requires a value.");
					description = args[++i];
					break;

				default:
					throw new ArgumentException($"Unrecognised argument: {args[i]}");
			}
		}

		var target = BuildTarget(attachPid, launchPath, launchUwp, arguments, description);
		return new LiveAppOptions { Target = target };
	}

	private static LiveAppTarget BuildTarget(int? attachPid, string? launchPath, string? launchUwp, string? arguments, string? description)
	{
		if (attachPid is { } pid)
		{
			return new LiveAppTarget { Kind = LiveAppTargetKind.AttachProcess, ProcessId = pid, Description = description ?? $"pid {pid}" };
		}

		if (launchUwp is { } aumid)
		{
			return new LiveAppTarget { Kind = LiveAppTargetKind.LaunchUwp, AppUserModelId = aumid, Arguments = arguments, Description = description ?? aumid };
		}

		if (launchPath is { } path)
		{
			return new LiveAppTarget { Kind = LiveAppTargetKind.LaunchExecutable, ExecutablePath = path, Arguments = arguments, Description = description ?? Path.GetFileName(path) };
		}

		throw new ArgumentException("A target is required: one of --attach, --launch, or --launch-uwp.");
	}
}
