using System.Collections.ObjectModel;
using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// One entry of the activity record as an agent's card and the Activity view show it: when, which tool, aimed at
/// what, how it ended and how long it took -- or a broker starting, as a marker in the list -- and, opened, its
/// arguments and response formatted and a write's diff.
/// <para>
/// The time column is when a call finished, or when a running one started; the duration is always the last
/// column, and counts up while the call runs. The body is read on demand, by the entry's id, because a listing
/// carries none: fifty bodies of source read back from disk on every refresh would be the window's whole cost.
/// </para>
/// </summary>
public sealed class OperationRow : Observable
{
	/// <summary>An error longer than this is cut on the row; the opened row has it whole.</summary>
	private const int ErrorLimit = 200;

	private string _time = string.Empty;
	private string _duration = string.Empty;
	private string _glyph = string.Empty;
	private bool _isRunning;
	private bool _succeeded;
	private bool _failed;
	private bool _cancelled;
	private string _error = string.Empty;
	private string _detail = string.Empty;
	private bool _isExpanded;
	private bool _isLoading;
	private string _bodyProblem = string.Empty;
	private string _arguments = string.Empty;
	private string _argumentsJson = string.Empty;
	private string _response = string.Empty;
	private string _notes = string.Empty;
	private string _callId = string.Empty;
	private bool _hasBody;
	private ActivityOutcome? _bodyOutcome;

	public OperationRow(ActivityEntry entry, DateTimeOffset now)
	{
		Id = entry.Id;
		IsMarker = entry.Kind == ActivityEntryKind.BrokerStarted;
		Tool = entry.Tool ?? string.Empty;
		Target = entry.Target ?? string.Empty;
		Workspace = entry.Workspace is { Length: > 0 } workspace ? Format.FileLine(workspace, line: null) : string.Empty;
		WorkspaceKey = entry.WorkspaceKey ?? string.Empty;
		DebugSession = entry.DebugSession ?? string.Empty;
		Client = AgentNames.Of(entry);
		ClientId = entry.ClientId;
		ClientKey = entry.ClientKey;
		MarkerText = IsMarker ? DescribeStart(entry) : string.Empty;

		Update(entry, now);
	}

	/// <summary>The entry's id, unique across broker runs. Rows are matched on it, so they survive a refresh.</summary>
	public string Id { get; }

	/// <summary>Whether this is a broker starting rather than a call: a marker in the list, not a gap.</summary>
	public bool IsMarker { get; }

	public bool IsCall => !IsMarker;

	/// <summary>What a marker says: that Rose started, which build, and which process.</summary>
	public string MarkerText { get; }

	/// <summary>The tool, by the name the client called it by.</summary>
	public string Tool { get; }

	/// <summary>What the call was aimed at in a few words; empty for the whole workspace.</summary>
	public string Target { get; }

	public bool HasTarget => Target.Length > 0;

	/// <summary>The solution the call was routed to, by its file name.</summary>
	public string Workspace { get; }

	/// <summary>The short key of the solution, for the tooltip.</summary>
	public string WorkspaceKey { get; }

	/// <summary>The debug session the call reached, where it reached one.</summary>
	public string DebugSession { get; }

	/// <summary>Where the call went, one line: the solution, else the debug session.</summary>
	public string Where => Workspace.Length > 0 ? Workspace : DebugSession.Length > 0 ? $"session {DebugSession}" : string.Empty;

	/// <summary>Which agent made the call, as the Activity view names it.</summary>
	public string Client { get; }

	/// <summary>The row the broker kept for the client that made the call, when the call ended.</summary>
	public string? ClientId { get; }

	/// <summary>The key the record files that client's calls under, which outlives a broker restart where the row does not.</summary>
	public string? ClientKey { get; }

	/// <summary>A note to the agent that made the call, about the call: what "Ask the agent about this" opens.</summary>
	public NoteComposer Composer { get; } = new();

	/// <summary>What a note about a call is about: the call in words, and its entry in the record.</summary>
	public static NoteSubject SubjectOf(ActivityEntry entry)
	{
		var call = string.Join(" ", new[] { entry.Tool, entry.Target }.Where(part => part is { Length: > 0 }));
		var readable = entry.Outcome switch
		{
			ActivityOutcome.Failed => $"{call}, which failed",
			ActivityOutcome.Running => $"{call}, still running",
			ActivityOutcome.Cancelled => $"{call}, which was cancelled",
			_ => call,
		};

		return new NoteSubject { Kind = NoteSubjectKind.ActivityCall, Readable = readable, ActivityEntry = entry.Id };
	}

	/// <summary>When it started, for ordering.</summary>
	public DateTimeOffset StartedUtc { get; private set; }

	/// <summary>When it was answered, for ordering; null while it runs and on a marker.</summary>
	public DateTimeOffset? FinishedUtc { get; private set; }

	/// <summary>When it finished, or when a running one started, as the time column says it.</summary>
	public string Time
	{
		get => _time;
		private set => Set(ref _time, value);
	}

	/// <summary>How long it took, or has taken so far: always the right-hand column.</summary>
	public string Duration
	{
		get => _duration;
		private set => Set(ref _duration, value);
	}

	/// <summary>One character for how it ended: running, succeeded, failed or cancelled.</summary>
	public string Glyph
	{
		get => _glyph;
		private set => Set(ref _glyph, value);
	}

	public bool IsRunning
	{
		get => _isRunning;
		private set => Set(ref _isRunning, value);
	}

	public bool Succeeded
	{
		get => _succeeded;
		private set => Set(ref _succeeded, value);
	}

	public bool Failed
	{
		get => _failed;
		private set => Set(ref _failed, value);
	}

	public bool Cancelled
	{
		get => _cancelled;
		private set => Set(ref _cancelled, value);
	}

	/// <summary>What the caller was told went wrong, its first line, cut.</summary>
	public string Error
	{
		get => _error;
		private set
		{
			Set(ref _error, value);
			OnPropertyChanged(nameof(HasError));
		}
	}

	public bool HasError => Error.Length > 0;

	/// <summary>A write previewed rather than applied, or the revision an answer was read at: what the row says after the target.</summary>
	public string Detail
	{
		get => _detail;
		private set
		{
			Set(ref _detail, value);
			OnPropertyChanged(nameof(HasDetail));
		}
	}

	public bool HasDetail => Detail.Length > 0;

	/// <summary>Whether the row is open, showing its body.</summary>
	public bool IsExpanded
	{
		get => _isExpanded;
		private set => Set(ref _isExpanded, value);
	}

	/// <summary>Whether its body is being read.</summary>
	public bool IsLoading
	{
		get => _isLoading;
		private set => Set(ref _isLoading, value);
	}

	/// <summary>Why the body could not be read, where it could not.</summary>
	public string BodyProblem
	{
		get => _bodyProblem;
		private set
		{
			Set(ref _bodyProblem, value);
			OnPropertyChanged(nameof(HasBodyProblem));
		}
	}

	public bool HasBodyProblem => BodyProblem.Length > 0;

	/// <summary>The arguments, indented.</summary>
	public string Arguments
	{
		get => _arguments;
		private set
		{
			Set(ref _arguments, value);
			OnPropertyChanged(nameof(HasArguments));
		}
	}

	public bool HasArguments => Arguments.Length > 0;

	/// <summary>The arguments exactly as the client sent them, which Copy as JSON copies.</summary>
	public string ArgumentsJson
	{
		get => _argumentsJson;
		private set => Set(ref _argumentsJson, value);
	}

	/// <summary>What the client was answered, indented where it is JSON.</summary>
	public string Response
	{
		get => _response;
		private set
		{
			Set(ref _response, value);
			OnPropertyChanged(nameof(HasResponse));
		}
	}

	public bool HasResponse => Response.Length > 0;

	/// <summary>What a reader should know about the entry itself, such as a body cut for its length.</summary>
	public string Notes
	{
		get => _notes;
		private set
		{
			Set(ref _notes, value);
			OnPropertyChanged(nameof(HasNotes));
		}
	}

	public bool HasNotes => Notes.Length > 0;

	/// <summary>The id the call carried through every process it crossed, which finds its lines in every log.</summary>
	public string CallId
	{
		get => _callId;
		private set
		{
			Set(ref _callId, value);
			OnPropertyChanged(nameof(HasCallId));
		}
	}

	public bool HasCallId => CallId.Length > 0;

	/// <summary>Each file a write changed, with its diff.</summary>
	public ObservableCollection<FileChangeView> Changes { get; } = [];

	public bool HasChanges => Changes.Count > 0;

	/// <summary>
	/// Whether the body has to be read: the row is open and holds none, or holds one read while the call ran, which
	/// says nothing of how it ended.
	/// </summary>
	public bool NeedsBody => IsExpanded && !IsLoading && IsCall && (!_hasBody || _bodyOutcome != Outcome);

	private ActivityOutcome? Outcome { get; set; }

	/// <summary>Brings the row in line with the entry as the record now has it.</summary>
	public void Update(ActivityEntry entry, DateTimeOffset now)
	{
		if (IsCall) Composer.Regard(SubjectOf(entry));

		StartedUtc = entry.StartedUtc;
		FinishedUtc = entry.FinishedUtc;
		Outcome = entry.Outcome;

		IsRunning = entry.Outcome == ActivityOutcome.Running;
		Succeeded = entry.Outcome == ActivityOutcome.Succeeded;
		Failed = entry.Outcome == ActivityOutcome.Failed;
		Cancelled = entry.Outcome == ActivityOutcome.Cancelled;
		Glyph = GlyphOf(entry);

		Time = Clock(entry.FinishedUtc ?? entry.StartedUtc);
		Error = Failed ? Shorten(entry.Error ?? "no reason given", entry.ErrorFullLength) : string.Empty;
		Detail = DetailOf(entry);

		Tick(now);
	}

	/// <summary>Moves a running call's duration on without asking the broker anything.</summary>
	public void Tick(DateTimeOffset now)
	{
		if (IsMarker)
		{
			Duration = string.Empty;
			return;
		}

		var elapsed = (FinishedUtc ?? now) - StartedUtc;
		if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

		Duration = IsRunning ? $"{Format.Duration(elapsed)}…" : Format.Duration(elapsed);
	}

	/// <summary>Opens the row, or closes it. Opening reads the body where the row has none; see <see cref="LoadBodyAsync"/>.</summary>
	public Task ToggleAsync(Func<string, CancellationToken, Task<ActivityEntry>> read, CancellationToken cancellationToken)
	{
		IsExpanded = !IsExpanded;

		return NeedsBody ? LoadBodyAsync(read, cancellationToken) : Task.CompletedTask;
	}

	/// <summary>
	/// Reads the entry with its body, by its id, and shows it. A failure is said on the row rather than thrown: a
	/// body that could not be read is a fact about one entry -- the record may have pruned it -- and not a reason
	/// to put an error over the whole window.
	/// </summary>
	public async Task LoadBodyAsync(Func<string, CancellationToken, Task<ActivityEntry>> read, CancellationToken cancellationToken)
	{
		if (IsMarker) return;

		IsLoading = true;
		BodyProblem = string.Empty;

		try
		{
			var full = await read(Id, cancellationToken);

			ShowBody(full);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			BodyProblem = $"Could not read this call's arguments and response: {exception.Message}";
		}
		finally
		{
			IsLoading = false;
		}
	}

	/// <summary>Shows an entry read with its body.</summary>
	public void ShowBody(ActivityEntry full)
	{
		_hasBody = true;
		_bodyOutcome = full.Outcome;

		ArgumentsJson = full.Arguments ?? string.Empty;
		Arguments = JsonText.Pretty(full.Arguments);
		Response = JsonText.Pretty(full.Response);

		if (full.Error is { Length: > 0 } error && full.Outcome == ActivityOutcome.Failed && string.IsNullOrEmpty(full.Response))
		{
			Response = error;
		}

		var notes = new List<string>(full.Notes);
		if (full.ArgumentsFullLength is { } arguments) notes.Add($"The arguments are cut; they were {arguments:N0} characters.");
		if (full.ResponseFullLength is { } response) notes.Add($"The response is cut; it was {response:N0} characters.");
		if (full.ErrorFullLength is { } errorLength) notes.Add($"The error is cut; it was {errorLength:N0} characters.");
		if (full.Outcome == ActivityOutcome.Running) notes.Add("The call is still running, so there is no response yet.");
		Notes = string.Join(Environment.NewLine, notes.Distinct(StringComparer.Ordinal));

		CallId = full.CorrelationId is { Length: > 0 } id
			? $"Call id {id}: every log line written for this call, in every process it crossed, carries it."
			: string.Empty;

		Changes.Clear();
		foreach (var change in full.Changes) Changes.Add(new FileChangeView(change));
		OnPropertyChanged(nameof(HasChanges));
	}

	/// <summary>
	/// The time column's text: a call's local time of day, to the second. A day is not named, because the list is
	/// newest first and a marker says where a run began.
	/// </summary>
	public static string Clock(DateTimeOffset utc) => utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

	/// <summary>The status glyph: a clock while running, a tick, a cross, or a slashed circle for a call its caller gave up on.</summary>
	public static string GlyphOf(ActivityEntry entry) => entry.Outcome switch
	{
		ActivityOutcome.Running => "◷",
		ActivityOutcome.Succeeded => "✓",
		ActivityOutcome.Failed => "✗",
		ActivityOutcome.Cancelled => "⊘",
		_ => "↻",
	};

	/// <summary>What a broker starting says: when, which build, which process.</summary>
	public static string DescribeStart(ActivityEntry entry)
	{
		var facts = new List<string> { "Rose started" };
		if (entry.BrokerBuild is { Length: > 0 } build) facts.Add(BuildIdentity.FromHandshake(build).Describe());
		if (entry.BrokerProcessId is { } pid) facts.Add(Format.Pid(pid));

		return string.Join(Format.Separator, facts);
	}

	private static string DetailOf(ActivityEntry entry)
	{
		if (entry.Applied == false) return "previewed, not applied";
		if (entry.Changes.Count > 0) return Format.Count(entry.Changes.Count, "file") + " changed";

		return entry.Revision is { } revision ? $"revision {revision}" : string.Empty;
	}

	private static string Shorten(string error, int? fullLength)
	{
		var firstLine = error.Split('\n')[0].Trim();
		var cut = firstLine.Length > ErrorLimit || fullLength is not null || firstLine.Length < error.TrimEnd().Length;

		if (firstLine.Length > ErrorLimit) firstLine = firstLine[..ErrorLimit];

		return cut ? firstLine + "…" : firstLine;
	}
}
