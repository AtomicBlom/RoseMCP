namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Where the RoseMCP app connects and which window it opens, read from the command line the tray or the broker
/// built: the RoseMCP window, or an Inspector window on a debug session.
/// <para>
/// Strict about what it does not recognise, the way <c>ServerOptions</c> is: a mistyped switch that
/// is silently ignored leaves a window pointed at the wrong port with nothing to say why. A
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

	/// <summary>Which session to open on, or null to open on the start page, where a person picks an app to debug.</summary>
	public string? SessionId { get; init; }

	/// <summary>
	/// The process being debugged, which is what an inspector is one of.
	/// <para>
	/// Passed on the command line even though the session implies it, because the single-instance
	/// key is claimed before this process has spoken to the broker: a key that has to be asked for
	/// is a key that cannot be claimed in time, and a second window would already exist.
	/// </para>
	/// </summary>
	public int? TargetProcessId { get; init; }

	/// <summary>
	/// Whether to open an Inspector window even with no session named, which then starts on the start page: the
	/// running apps to attach to, a launch form and the targets debugged lately. What "Debug an app" asks for; a launch naming nothing opens
	/// the RoseMCP window instead.
	/// </summary>
	public bool Inspect { get; init; }

	/// <summary>Whether this launch asks for an Inspector window rather than the RoseMCP window.</summary>
	public bool OpensInspector => Inspect || SessionId is { Length: > 0 } || TargetProcessId is not null;

	/// <summary>Where the operator API is, composed from the host and port.</summary>
	public Uri BaseAddress => new($"http://{Host}:{Port}");

	/// <summary>
	/// What one RoseMCP app is one of: the broker it reads, named by where it listens.
	/// <para>
	/// One app per broker rather than one per window, because the app holds every window -- the RoseMCP
	/// window and an Inspector window per debugged process -- and a second launch against the same broker is
	/// asking for one of those, which the running app opens or brings forward. The broker rather than the
	/// machine, because a broker on a scratch port beside the installed one is a different set of agents and
	/// workspaces, and a window showing one while launched for the other would be confidently wrong.
	/// </para>
	/// </summary>
	public string InstanceKey => $"RoseMcp.App:{Host.ToLowerInvariant()}:{Port}";

	/// <summary>
	/// The command that opens the RoseMCP window against this broker, for a person to run themselves or keep in a
	/// shortcut: the app's executable, quoted for the space an install path routinely has, then where to connect
	/// and the token. Valid only for this run of the broker, since the token is minted per run.
	/// </summary>
	public string CommandLine(string executablePath)
	{
		var arguments = new List<string> { $"\"{executablePath}\"", "--host", Host, "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
		if (Token is { Length: > 0 } token) arguments.AddRange(["--token", token]);

		return string.Join(' ', arguments);
	}

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

				case "--target-pid":
					if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var targetPid))
					{
						throw new ArgumentException("--target-pid requires the process id being debugged.");
					}

					options = options with { TargetProcessId = targetPid };
					i++;
					break;

				case "--inspect":
					options = options with { Inspect = true };
					break;

				default:
					throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
			}
		}

		return options;
	}
}
