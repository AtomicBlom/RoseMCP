using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One stop as the Inspector's code view reads it: every thread, the one whose stack is shown, and that stack.
/// <para>
/// Any thread's stack can be read, not only the one that stopped: a deadlock is a shape across threads, and the
/// thread holding the lock is rarely the one a breakpoint caught. The stopped thread is shown first because it is
/// where the stop is.
/// </para>
/// <para>
/// Made per stop and never carried into the next, for the reason a <see cref="StopInspection"/> is: a thread's
/// position and a frame's index are valid only within the stop they were read at. An answer that arrives for a stop
/// that has moved on, or for a thread no longer chosen, is dropped rather than shown as this stop's. Once the target
/// moves the rows stay on screen under a banner, and nothing on them can be asked about, since what an agent would be
/// told to look up is gone.
/// </para>
/// </summary>
public sealed class CodeInspection : Observable
{
	private IReadOnlyList<ThreadRow> _threads = [];
	private string _threadsDetail = string.Empty;
	private ThreadRow? _selectedThread;
	private StopInspection? _stack;
	private bool _isOver;
	private string _location = string.Empty;

	public CodeInspection(string sessionId, long stopSequence, int? stoppedThreadId)
	{
		SessionId = sessionId;
		StopSequence = stopSequence;
		StoppedThreadId = stoppedThreadId;
	}

	public string SessionId { get; }

	/// <summary>The stop this was read at.</summary>
	public long StopSequence { get; }

	/// <summary>The thread the stop is on, which is the one shown until another is chosen.</summary>
	public int? StoppedThreadId { get; }

	/// <summary>Every managed thread at this stop, the stopped one first as the host lists them.</summary>
	public IReadOnlyList<ThreadRow> Threads
	{
		get => _threads;
		private set => Set(ref _threads, value);
	}

	/// <summary>Why the thread list is empty or partial, when it is.</summary>
	public string ThreadsDetail
	{
		get => _threadsDetail;
		private set => Set(ref _threadsDetail, value);
	}

	/// <summary>The thread whose stack is shown, once the threads are read.</summary>
	public ThreadRow? SelectedThread
	{
		get => _selectedThread;
		private set
		{
			Set(ref _selectedThread, value);
			OnPropertyChanged(nameof(StackThreadId));
			OnPropertyChanged(nameof(StackCaption));
		}
	}

	/// <summary>The thread whose stack is asked for: the one chosen, else the stopped one.</summary>
	public int? StackThreadId => SelectedThread?.Id ?? StoppedThreadId;

	/// <summary>The stack of <see cref="StackThreadId"/>, once read.</summary>
	public StopInspection? Stack
	{
		get => _stack;
		private set => Set(ref _stack, value);
	}

	/// <summary>What the stack heading says: whose stack it is.</summary>
	public string StackCaption => StackThreadId is { } thread
		? thread == StoppedThreadId ? $"Stack · thread {thread}, where it stopped" : $"Stack · thread {thread}"
		: "Stack";

	/// <summary>
	/// Where the stop is, as the header names it: the stopped thread's top frame's file and line, or its method where
	/// symbols could not say. Empty until that stack is read, and kept while another thread's is shown.
	/// </summary>
	public string Location
	{
		get => _location;
		private set => Set(ref _location, value);
	}

	/// <summary>Whether the target has moved on from this stop, so nothing here can be asked about.</summary>
	public bool IsOver
	{
		get => _isOver;
		private set => Set(ref _isOver, value);
	}

	/// <summary>Says the target has moved on from this stop.</summary>
	public void End() => IsOver = true;

	/// <summary>
	/// What a note about <paramref name="thread"/> is about, or null where it is not one of this stop's rows or the stop
	/// is over: a row from an earlier stop names a thread at a place it no longer is.
	/// </summary>
	public NoteSubject? SubjectOf(ThreadRow thread) =>
		!IsOver && Threads.Contains(thread) ? AskAgent.Thread(SessionId, thread) : null;

	/// <summary>
	/// What a note about <paramref name="frame"/> is about, or null where it is not a frame of the stack shown at this
	/// stop: a frame index from an earlier stop or another thread names a different frame now.
	/// </summary>
	public NoteSubject? SubjectOf(FrameRow frame) =>
		!IsOver && Stack is { } stack && stack.Frames.Contains(frame) ? AskAgent.Frame(SessionId, stack.ThreadId, frame) : null;

	/// <summary>
	/// What a note about <paramref name="value"/> is about, or null where it is not in the values of the frame shown at
	/// this stop: a value path read in an earlier stop, or another frame, reads something else now.
	/// </summary>
	public NoteSubject? SubjectOf(VariableNode value) =>
		!IsOver && Stack is { } stack && Holds(stack.Variables, value) ? AskAgent.Value(SessionId, stack.ThreadId, stack.Selected, value) : null;

	private static bool Holds(IEnumerable<VariableNode> values, VariableNode value) =>
		values.Any(candidate => ReferenceEquals(candidate, value) || Holds(candidate.Children, value));

	/// <summary>
	/// Takes the thread list read at this stop, choosing the thread already chosen where it is still there, else the
	/// stopped one. An answer from another stop, or one saying the target is running, leaves the list as it was.
	/// </summary>
	/// <returns>Whether it was taken.</returns>
	public bool Take(LiveThreadList threads)
	{
		if (!IsThisStop(threads))
		{
			ThreadsDetail = threads.Detail ?? InspectorText.NotStopped;
			return false;
		}

		var rows = threads.Threads.Select(thread => new ThreadRow(thread)).ToList();
		var wanted = StackThreadId;

		Threads = rows;
		ThreadsDetail = threads.Detail ?? string.Empty;
		SelectedThread = rows.FirstOrDefault(row => row.Id == wanted) ?? rows.FirstOrDefault(row => row.IsStopped) ?? rows.FirstOrDefault();

		return true;
	}

	/// <summary>Chooses whose stack is shown. The stack shown goes until the chosen thread's is read.</summary>
	/// <returns>Whether that changed anything, which is when a read is owed.</returns>
	public bool Choose(ThreadRow thread)
	{
		if (ReferenceEquals(thread, SelectedThread) && Stack?.ThreadId == thread.Id) return false;

		SelectedThread = thread;
		if (Stack?.ThreadId != thread.Id) Stack = null;

		return true;
	}

	/// <summary>
	/// Takes a stack read at this stop for the thread now chosen. One for another stop, another thread, or a target
	/// that is running is dropped: a reader clicking down the thread list starts a read per click, and they do not come
	/// back in the order they were asked for.
	/// </summary>
	/// <returns>The stack taken, or null where it was dropped.</returns>
	public StopInspection? Take(LiveStackFrames frames)
	{
		if (!IsThisStop(frames)) return null;

		// Until a thread is chosen, the stack asked for is the stopped thread's, whichever id the host gives it.
		var chosen = SelectedThread?.Id;
		var forThread = frames.ThreadId ?? StoppedThreadId;
		if (chosen is not null && forThread != chosen) return null;

		var stack = new StopInspection(frames);
		Stack = stack;

		var stoppedThread = chosen is null || chosen == StoppedThreadId || SelectedThread?.IsStopped == true;
		if (stoppedThread && stack.Frames.FirstOrDefault() is { } top) Location = top.Line is not null && top.HasWhere ? top.Where.Split(" · ")[0] : top.Method;

		return stack;
	}

	private bool IsThisStop(LiveExecutionReport report) =>
		report.Execution != LiveExecutionState.Running && report.Stop?.EventSequence == StopSequence;
}
