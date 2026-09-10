namespace RoseMcp.Tray;

/// <summary>Where the in-process broker listens.</summary>
public sealed record TrayOptions
{
	public string Host { get; init; } = "127.0.0.1";

	public int Port { get; init; } = 5077;

	public string? WorkerPath { get; init; }

	/// <summary>
	/// Where the inspector executable is, when it is somewhere the usual search would not look.
	/// Null means look: beside this install, then in the repository.
	/// </summary>
	public string? InspectorPath { get; init; }

	public static TrayOptions Parse(string[] args)
	{
		var options = new TrayOptions();

		for (var i = 0; i < args.Length - 1; i++)
		{
			options = args[i] switch
			{
				"--host" => options with { Host = args[i + 1] },
				"--port" when int.TryParse(args[i + 1], out var port) => options with { Port = port },
				"--worker" => options with { WorkerPath = args[i + 1] },
				"--inspector" => options with { InspectorPath = args[i + 1] },
				_ => options,
			};
		}

		return options;
	}
}
