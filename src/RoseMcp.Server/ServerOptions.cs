namespace RoseMcp.Server;

/// <summary>Which transport to serve on, and where.</summary>
public sealed record ServerOptions
{
	public bool UseHttp { get; init; }

	/// <summary>
	/// Loopback by default and deliberately. This server reads any file it can reach and rewrites
	/// source, so it is not something to expose on a network interface by accident.
	/// </summary>
	public string Host { get; init; } = "127.0.0.1";

	public int Port { get; init; } = 5077;

	public string? WorkerPath { get; init; }

	public bool NoRestore { get; init; }

	public static ServerOptions Parse(string[] args)
	{
		var useHttp = false;
		var host = "127.0.0.1";
		var port = 5077;
		string? workerPath = null;
		var noRestore = false;

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--transport":
					if (i + 1 >= args.Length) throw new ArgumentException("--transport requires stdio or http.");
					useHttp = args[++i].Equals("http", StringComparison.OrdinalIgnoreCase);
					break;

				case "--host":
					if (i + 1 >= args.Length) throw new ArgumentException("--host requires an address.");
					host = args[++i];
					break;

				case "--port":
					if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port))
					{
						throw new ArgumentException("--port requires a number.");
					}

					i++;
					break;

				case "--worker":
					if (i + 1 >= args.Length) throw new ArgumentException("--worker requires a path.");
					workerPath = args[++i];
					break;

				case "--no-restore":
					noRestore = true;
					break;

				default:
					throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
			}
		}

		var options = new ServerOptions
		{
			UseHttp = useHttp,
			Host = host,
			Port = port,
			WorkerPath = workerPath,
			NoRestore = noRestore,
		};

		options.Validate();
		return options;
	}

	private void Validate()
	{
		if (!UseHttp) return;

		var loopback = Host is "127.0.0.1" or "::1" or "localhost";
		var token = Environment.GetEnvironmentVariable("ROSEMCP_TOKEN");

		if (!loopback && string.IsNullOrWhiteSpace(token))
		{
			throw new ArgumentException(
				$"Refusing to bind {Host}: this server reads and rewrites source anywhere it can reach. "
					+ "Bind 127.0.0.1, or set ROSEMCP_TOKEN to require a bearer token.");
		}
	}
}
