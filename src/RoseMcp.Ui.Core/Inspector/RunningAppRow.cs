using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One running .NET app on an Inspector's start page: what it is, which framework it runs, and whether it is being
/// debugged -- in which case its action opens that session rather than attaching a second debugger, since a process
/// has one.
/// </summary>
public sealed class RunningAppRow : Observable
{
	private string _displayName = string.Empty;
	private string _kind = string.Empty;
	private string _status = string.Empty;
	private string _action = string.Empty;
	private string? _sessionId;
	private bool _isDebugged;

	public RunningAppRow(RunningApp app)
	{
		ProcessId = app.ProcessId;

		Update(app);
	}

	/// <summary>Identity of the row, and what attaching names.</summary>
	public int ProcessId { get; }

	/// <summary>What the row calls the process: its window's title, else its name.</summary>
	public string DisplayName
	{
		get => _displayName;
		private set => Set(ref _displayName, value);
	}

	/// <summary>The process id, as a row shows it.</summary>
	public string Pid => ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>The framework it runs, and its architecture.</summary>
	public string Kind
	{
		get => _kind;
		private set => Set(ref _kind, value);
	}

	/// <summary>Whether it is being debugged, by whom, and whether it is stopped.</summary>
	public string Status
	{
		get => _status;
		private set => Set(ref _status, value);
	}

	/// <summary>Open, where a session is debugging it; Attach otherwise.</summary>
	public string Action
	{
		get => _action;
		private set => Set(ref _action, value);
	}

	/// <summary>The session debugging it, which its action opens; null where none is.</summary>
	public string? SessionId
	{
		get => _sessionId;
		private set => Set(ref _sessionId, value);
	}

	/// <summary>Whether a session is debugging it.</summary>
	public bool IsDebugged
	{
		get => _isDebugged;
		private set => Set(ref _isDebugged, value);
	}

	public void Update(RunningApp app)
	{
		DisplayName = app.WindowTitle is { Length: > 0 } title ? title : app.Name;
		Kind = DescribeKind(app);
		Status = DescribeStatus(app);
		SessionId = app.SessionId;
		IsDebugged = app.SessionId is not null;
		Action = IsDebugged ? "Open" : "Attach";
	}

	/// <summary>
	/// Whether an app answers what was typed into the search: its name, title, path, package or process id contains
	/// it, ignoring case. Nothing typed matches everything.
	/// </summary>
	public static bool Matches(RunningApp app, string? search)
	{
		ArgumentNullException.ThrowIfNull(app);

		if (string.IsNullOrWhiteSpace(search)) return true;

		var typed = search.Trim();
		string?[] fields = [app.Name, app.WindowTitle, app.ExecutablePath, app.AppUserModelId, app.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)];

		return fields.Any(field => field?.Contains(typed, StringComparison.OrdinalIgnoreCase) == true);
	}

	/// <summary>The framework and the architecture, which together say what the Inspector can do with it.</summary>
	public static string DescribeKind(RunningApp app)
	{
		var framework = app.XamlStack switch
		{
			XamlStack.Uwp => "UWP",
			XamlStack.WinUi => "WinUI 3",
			XamlStack.Wpf => "WPF",
			_ => ".NET",
		};

		var architecture = app.Architecture == TargetArchitecture.Unknown ? null : app.Architecture.ToString().ToLowerInvariant();
		var window = app.HasWindow ? null : "no window";

		return string.Join(" · ", new[] { framework, architecture, window }.OfType<string>());
	}

	/// <summary>Whether it is being debugged, as a row says it: by you or by an agent, and stopped or not.</summary>
	public static string DescribeStatus(RunningApp app)
	{
		if (app.SessionId is null) return "Not being debugged";

		var who = app.SessionStartedBy == LiveAppStarter.Person ? "you" : "an agent";
		var state = app.SessionState == LiveAppSessionState.Starting ? " · starting" : app.SessionStopped ? " · stopped" : string.Empty;

		return $"Being debugged by {who}{state}";
	}
}
