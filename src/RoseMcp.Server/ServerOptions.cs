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

	/// <summary>
	/// Host the broker in this process whatever else is running: never relay to a tray, and never hand
	/// the session to another broker. What an installed relay starts its child broker with, so the
	/// child cannot relay in turn.
	/// </summary>
	public bool NoRelay { get; init; }

	/// <summary>
	/// The environment variable that points the broker's per-machine records -- the recent debug targets, the accepted
	/// workspace reasons, and the activity record where no log root moves it -- at another folder than this user's
	/// local application data.
	/// </summary>
	public const string LocalAppDataVariable = RoseMcp.Settings.RoseSettingsFile.LocalAppDataVariable;

	/// <summary>
	/// Where the broker keeps its per-machine records instead of this user's local application data, from
	/// <see cref="LocalAppDataVariable"/>; null for the user's own. What a test gives a server it starts, so that
	/// server never writes the records the person using the machine relies on.
	/// </summary>
	public string? LocalAppData { get; init; } = Configured(Environment.GetEnvironmentVariable(LocalAppDataVariable));

	/// <summary>A configured folder, or null where the variable is unset or blank.</summary>
	private static string? Configured(string? folder) => string.IsNullOrWhiteSpace(folder) ? null : folder;

	public static ServerOptions Parse(string[] args)
	{
		var useHttp = false;
		var host = "127.0.0.1";
		var port = 5077;
		string? workerPath = null;
		var noRestore = false;
		var noRelay = false;

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

				case "--no-relay":
					noRelay = true;
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
			NoRelay = noRelay,
		};

		options.Validate();
		return options;
	}

	/// <summary>
	/// What a relay starts the install's broker with when no tray is running: this session's own
	/// settings, and <c>--no-relay</c> so the child hosts the broker rather than looking for a tray.
	/// </summary>
	public IReadOnlyList<string> ChildBrokerArguments()
	{
		var arguments = new List<string> { "--no-relay", "--host", Host, "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };

		if (WorkerPath is not null) arguments.AddRange(["--worker", WorkerPath]);
		if (NoRestore) arguments.Add("--no-restore");

		return arguments;
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
