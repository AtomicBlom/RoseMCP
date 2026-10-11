using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// What the RoseMCP window reads of the broker while it is open: the clients, the workspaces, the debug sessions,
/// the newest of the activity record and the notes the person has sent agents, each held current by a long poll;
/// the memory, on a timer; and which build the broker is, asked whenever the broker is reached again.
/// <para>
/// One feed for the window rather than one per view, because the window shows a count from three of them in its
/// view list whichever view is open, and the Agents and Workspaces views both join the clients to the record. Five
/// long polls are five requests held open on a loopback port, and the feed runs only while the window is on screen:
/// <see cref="Stop"/> cancels every one of them, which is what a window closing or being minimised calls.
/// </para>
/// <para>
/// Whether Rose is serving is the feed's to say, from every read at once: any read that fails says why, and the
/// next that succeeds says it is back. A broker that restarts is a broker that is not there for a moment and then
/// is, with a new token -- which the app takes from the launch the tray hands it, and <see cref="Kick"/> puts to use.
/// </para>
/// </summary>
public sealed class RoseFeed
{
	/// <summary>
	/// How many of the newest entries the feed holds: what the Agents view lists each agent's calls from and the
	/// Workspaces view says who used each workspace from. Enough for a morning's work by a handful of agents; the
	/// Activity view reads further back on request.
	/// </summary>
	public const int ActivityWindow = 300;

	/// <summary>How often memory is sampled. Every figure moves on every read, so nothing is gained by waiting on it.</summary>
	public static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(5);

	private readonly OperatorClient _client;
	private readonly ViewWatch<ClientList> _clients;
	private readonly ViewWatch<OperatorWorkspaceList> _workspaces;
	private readonly ViewWatch<OperatorSessionList> _sessions;
	private readonly ViewWatch<ActivityPage> _activity;
	private readonly ViewWatch<NoteList> _notes;
	private readonly PollLoop _memory;
	private bool _askingHello;

	public RoseFeed(OperatorClient client)
	{
		_client = client;

		_clients = new ViewWatch<ClientList>(
			(after, wait, cancellationToken) => client.ClientsAsync(after, wait, cancellationToken),
			view => view.Version,
			view => Took(() => Clients = view),
			Failed);

		_workspaces = new ViewWatch<OperatorWorkspaceList>(
			(after, wait, cancellationToken) => client.WorkspacesAsync(after, wait, cancellationToken),
			view => view.Version,
			view => Took(() => Workspaces = view),
			Failed);

		_sessions = new ViewWatch<OperatorSessionList>(
			(after, wait, cancellationToken) => client.SessionListAsync(after, wait, cancellationToken),
			view => view.Version,
			view => Took(() => Sessions = view),
			Failed);

		_activity = new ViewWatch<ActivityPage>(
			(after, wait, cancellationToken) => client.ActivityAsync(new ActivityFilter { Limit = ActivityWindow }, before: null, after, wait, cancellationToken),
			view => view.Version,
			view => Took(() => Activity = view),
			Failed);

		_notes = new ViewWatch<NoteList>(
			(after, wait, cancellationToken) => client.NotesAsync(after, wait, cancellationToken),
			view => view.Version,
			view => Took(() => Notes = view),
			Failed);

		_memory = new PollLoop(ReadMemoryAsync, MemoryInterval, Failed);
	}

	/// <summary>Any of the five views changed. Raised on the context the feed was started from.</summary>
	public event Action? Changed;

	/// <summary>A memory sample arrived.</summary>
	public event Action? MemoryChanged;

	/// <summary>Whether Rose is serving changed, or what it says about why not, or which build it is.</summary>
	public event Action? ConnectionChanged;

	public ClientList Clients { get; private set; } = new() { Version = string.Empty };

	public OperatorWorkspaceList Workspaces { get; private set; } = new() { Version = string.Empty };

	public OperatorSessionList Sessions { get; private set; } = new() { Version = string.Empty };

	public ActivityPage Activity { get; private set; } = new() { Bodies = false, Matched = 0, Retention = TimeSpan.Zero };

	/// <summary>The notes the person has sent agents, with where each has got to and what the agent answered.</summary>
	public NoteList Notes { get; private set; } = new() { Version = string.Empty };

	/// <summary>The last memory sample, or null before the first.</summary>
	public MemoryReport? Memory { get; private set; }

	/// <summary>Which build the broker is, as it last said; null until it has.</summary>
	public OperatorHello? Hello { get; private set; }

	/// <summary>Whether the last read reached Rose.</summary>
	public bool IsServing { get; private set; }

	/// <summary>Why Rose is not being reached, in words; null while it is.</summary>
	public string? Problem { get; private set; }

	/// <summary>What the Agents view joins to its cards to say what each agent is using.</summary>
	public AgentContext Context => new(Workspaces.Workspaces, Sessions.Sessions);

	/// <summary>Starts every read, each at once. Starting a running feed reads again at once.</summary>
	public void Start()
	{
		KickViews();

		_clients.Start();
		_workspaces.Start();
		_sessions.Start();
		_activity.Start();
		_notes.Start();
		_memory.Start();
	}

	/// <summary>Stops every read and cancels each one in flight.</summary>
	public void Stop()
	{
		_clients.Stop();
		_workspaces.Stop();
		_sessions.Stop();
		_activity.Stop();
		_notes.Stop();
		_memory.Stop();
	}

	/// <summary>Reads everything again at once: after a new token, or after something this window did changed a view.</summary>
	public void Kick()
	{
		KickViews();
		_memory.Kick();
	}

	private void KickViews()
	{
		_clients.Kick();
		_workspaces.Kick();
		_sessions.Kick();
		_activity.Kick();
		_notes.Kick();
	}

	private void Took(Action keep)
	{
		keep();
		Served();
		Changed?.Invoke();
	}

	private async Task ReadMemoryAsync(CancellationToken cancellationToken)
	{
		Memory = await _client.MemoryAsync(cancellationToken);
		Served();
		MemoryChanged?.Invoke();
	}

	private void Served()
	{
		if (IsServing) return;

		IsServing = true;
		Problem = null;
		ConnectionChanged?.Invoke();

		// Whenever Rose is reached again, because a broker that went away and came back may be another build.
		_ = AskHelloAsync();
	}

	private void Failed(Exception exception)
	{
		var problem = RoseText.Problem(exception, _client.HasToken, _client.BaseAddress);
		if (!IsServing && problem == Problem) return;

		IsServing = false;
		Problem = problem;
		ConnectionChanged?.Invoke();
	}

	private async Task AskHelloAsync()
	{
		if (_askingHello) return;

		_askingHello = true;

		try
		{
			Hello = await _client.HelloAsync(CancellationToken.None);
			ConnectionChanged?.Invoke();
		}
		catch (Exception exception) when (exception is OperatorException or ObjectDisposedException)
		{
			// The views say why Rose cannot be reached, in better words, and ask again when it can.
		}
		finally
		{
			_askingHello = false;
		}
	}
}
