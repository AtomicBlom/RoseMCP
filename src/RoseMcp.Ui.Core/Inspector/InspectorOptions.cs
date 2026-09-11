namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Where the inspector connects and what it opens on, read from the command line the tray built.
/// <para>
/// Strict about what it does not recognise, the way <c>ServerOptions</c> is: a mistyped switch that
/// is silently ignored leaves a window connected to the wrong port with nothing to say why. A
/// missing token is not an error, though -- somebody running the exe by hand gets a window that
/// explains how to get one, which is more use than a process that exits.
/// </para>
/// </summary>
public sealed record InspectorOptions
{
	/// <summary>Loopback only. The operator surface is never reachable across a network.</summary>
	public string Host { get; init; } = "127.0.0.1";

	public int Port { get; init; } = 5077;

	/// <summary>
	/// The bearer token the tray minted for this run, or null when the inspector was started
	/// without one. Null is a state the window explains rather than a failure to start.
	/// </summary>
	public string? Token { get; init; }

	/// <summary>Which session to open on, or null to show the list and wait for a choice.</summary>
	public string? SessionId { get; init; }

	/// <summary>Where the operator API is, composed from the host and port.</summary>
	public Uri BaseAddress => new($"http://{Host}:{Port}");

	/// <exception cref="ArgumentException">A switch is unrecognised, or one is missing its value.</exception>
	public static InspectorOptions Parse(string[] args)
	{
		var options = new InspectorOptions();

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--host":
					if (i + 1 >= args.Length) throw new ArgumentException("--host requires an address.");
					options = options with { Host = args[++i] };
					break;

				case "--port":
					if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var port))
					{
						throw new ArgumentException("--port requires a number.");
					}

					options = options with { Port = port };
					i++;
					break;

				case "--token":
					if (i + 1 >= args.Length) throw new ArgumentException("--token requires the tray's operator token.");
					options = options with { Token = args[++i] };
					break;

				case "--session":
					if (i + 1 >= args.Length) throw new ArgumentException("--session requires a session id.");
					options = options with { SessionId = args[++i] };
					break;

				default:
					throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
			}
		}

		return options;
	}
}
