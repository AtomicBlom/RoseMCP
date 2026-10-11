namespace RoseMcp.Contracts;

/// <summary>
/// One running .NET process a person could debug, as <c>GET /operator/debug/apps</c> lists it: what it is, what it
/// runs, and the session already debugging it, where there is one -- a process has one debugger, so that session is
/// what opening it reaches.
/// </summary>
public sealed record RunningApp
{
	/// <summary>The process, by the id an attach names it by.</summary>
	public required int ProcessId { get; init; }

	/// <summary>The process's own name, without its extension.</summary>
	public required string Name { get; init; }

	/// <summary>The title of its window, where it has one with a title.</summary>
	public string? WindowTitle { get; init; }

	/// <summary>The executable it runs from, where it could be read.</summary>
	public string? ExecutablePath { get; init; }

	/// <summary>Which XAML framework it has loaded, read from its modules; <see cref="XamlStack.Unknown"/> for none.</summary>
	public XamlStack XamlStack { get; init; }

	/// <summary>The packaged app it is, where it runs with a package identity: what launching it again would name.</summary>
	public string? AppUserModelId { get; init; }

	/// <summary>The architecture it runs as, which decides which host would debug it.</summary>
	public TargetArchitecture Architecture { get; init; }

	/// <summary>Whether it has a window on the desktop. False only in a list that asked for every .NET process.</summary>
	public bool HasWindow { get; init; }

	/// <summary>The open session debugging it, where there is one.</summary>
	public string? SessionId { get; init; }

	/// <summary>That session's state.</summary>
	public LiveAppSessionState? SessionState { get; init; }

	/// <summary>Who started that session.</summary>
	public LiveAppStarter? SessionStartedBy { get; init; }

	/// <summary>Whether that session's target is stopped.</summary>
	public bool SessionStopped { get; init; }
}
