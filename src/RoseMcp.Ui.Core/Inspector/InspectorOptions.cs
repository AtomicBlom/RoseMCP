namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// Where the inspector connects and what it opens on, read from the command line the tray built.
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

	/// <summary>Which session to open on, or null to adopt the one session there is.</summary>
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

	/// <summary>Where the operator API is, composed from the host and port.</summary>
	public Uri BaseAddress => new($"http://{Host}:{Port}");

	/// <summary>
	/// What one inspector is one of: the process being debugged, else the session, else the app.
	/// <para>
	/// The target rather than the session, because detaching and attaching again is the same
	/// program being looked at and should be the same window -- and because the title names the
	/// process, so two windows keyed on sessions would be two windows with one name.
	/// </para>
	/// </summary>
	public string InstanceKey => TargetProcessId is { } pid
		? $"RoseMcp.Inspector:pid:{pid}"
		: SessionId is { Length: > 0 } session ? $"RoseMcp.Inspector:session:{session}" : "RoseMcp.Inspector";

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

				default:
					throw new ArgumentException($"Unrecognised argument '{args[i]}'.");
			}
		}

		return options;
	}
}
