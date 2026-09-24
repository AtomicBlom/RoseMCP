using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.XamlDiff;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Asks a XAML diagnostics provider what the target's live tree contains, and takes the request
/// lock that every answer is served under. Every request is one message on the pipe <see
/// cref="XamlProviderSession"/> establishes, with <see cref="XamlProviderWire"/> holding the wire
/// format in both directions and <see cref="XamlApply"/> doing the one thing here that remembers
/// anything between calls.
/// <para>
/// One request at a time, and the lock is re-entrant. The host serves MCP calls concurrently while
/// every XAML request shares one pipe, which carries one request and one reply at a time, and two at
/// once produced a tree of 22 elements where the app has 24 -- a truncated tree handing out handles
/// for a tree that is not there. Re-entrant because selecting by handle finishes by reading the
/// selection, which takes the lock again on the same thread.
/// </para>
/// </summary>
internal sealed class XamlDiagnosticsSession : IDisposable
{
	// What a tree read reports about which channel answered. Named constants rather than literals at
	// the two return sites, because the whole value of the field is that a test can tell the two
	// apart, and a test comparing against a literal spelled differently in one place would pass
	// while reporting the wrong channel.
	private const string PipeChannel = "pipe";

	// The provider's residency in the target, and the only route to it. Held rather than made per
	// request because one injection per session is the invariant: a second tap in the app receives
	// every mutation in it and holds a copy of its tree, for nothing.
	private readonly XamlProviderSession _provider;

	// The apply path and the baselines it keeps. Its own object because it is the only part of this
	// session that carries state between calls: what the app was last told, per file.
	private readonly XamlApply _apply;

	// How long the provider may take to answer one request. Every other bound is a wait on getting a
	// provider resident at all, which is the provider session's business; this is the only one a
	// request on a standing pipe can hit.
	private TimeSpan Reply => _provider.Bounds.Snapshot;

	// One request at a time, and this is measured rather than defensive (#93). The host serves MCP
	// calls concurrently -- two tree reads issued together finish in the time of one, where serialised
	// they would take twice as long -- and every request here shares one pipe, which carries one
	// request and one reply at a time.
	//
	// The measurement was taken against a channel of files and the conclusion outlived it. On ten
	// concurrent pairs against the probe: several fifteen-second waits for a snapshot the other call had
	// already consumed, and once a tree of 22 elements where the app has 24, returned with no detail
	// set. That last one is why this is a lock and not a documented limitation: a truncated tree
	// reported as success feeds handles to every other tool. A pipe fails differently and no better --
	// two requests interleaved on one stream pair each reply with the wrong question.
	//
	// Serialising rather than giving each request a channel of its own, because the provider does
	// everything on the app's UI thread. A second pipe would buy no parallelism from a single-threaded
	// consumer. The wait can be long -- the endpoint timeout is twenty seconds -- and a slow correct
	// answer is the trade being made.
	//
	// Every public entry point takes it once and calls a Core method that assumes it is held, and so
	// does every call into the provider session, so no path takes it twice and its re-entrancy is not
	// relied on. That is worth keeping rather than merely true: a Core method that takes the lock
	// itself would deadlock under a SemaphoreSlim and pass under this one, so the pairing is what
	// makes the choice of lock free rather than load-bearing.
	private readonly Lock _requests = new();

	private readonly ILogger _logger;

	/// <summary>
	/// Written out rather than a primary constructor, because the apply is built from the provider
	/// beside it and a field initialiser cannot reach a sibling field.
	/// </summary>
	internal XamlDiagnosticsSession(ILogger logger)
	{
		_logger = logger;
		_provider = new XamlProviderSession(logger);
		_apply = new XamlApply(_provider, logger);
	}

	/// <summary>
	/// Reads a snapshot of the target's live visual tree, injecting the provider first. Returns a tree
	/// with a <see cref="LiveXamlTree.Detail"/> and no nodes -- never throws -- when the provider is not
	/// available for this architecture, injection fails, or the target has no XAML UI.
	/// </summary>
	public LiveXamlTree ReadTree(int pid)
	{
		lock (_requests) return ReadTreeCore(pid);
	}

	/// <summary>
	/// Which XAML framework this session's target turned out to be, once anything has asked. Null until
	/// the first request resolves a tap, because nothing before that has needed to know.
	/// <para>
	/// Exposed so a session's self-report can prefer what a real request found over its own probe. The
	/// two read the same module list and normally agree; where they do not, this one is the answer that
	/// a tap was actually chosen by.
	/// </para>
	/// </summary>
	public XamlStackDetection? Stack => _provider.Stack;

	/// <summary>
	/// Whether a provider is resident, which is what decides the cost of the next request: a message
	/// to a reader already in the app, or an injection first.
	/// <para>
	/// Three states rather than a bool, because a pipe that has dropped is not the same as one that was
	/// never opened. Nothing injected is the ordinary state of a session nobody has asked about XAML;
	/// a provider that has gone is a channel failing, and the next request will inject a second tap.
	/// </para>
	/// </summary>
	public LiveXamlProvider Provider => _provider.Residency;

	private LiveXamlTree ReadTreeCore(int pid)
	{
		var (pipe, unready) = _provider.Connect(pid);
		if (pipe is null) return new LiveXamlTree { Detail = unready };

		var request = "tree";

		var served = pipe.Request(request, Reply);
		if (served is null) return new LiveXamlTree { Detail = Unanswered(request, "a tree") };

		var nodes = XamlProviderWire.ParseTree(served.Split('\n', StringSplitOptions.RemoveEmptyEntries));
		_logger.LogInformation("Read a XAML tree of {Count} element(s) from pid {Pid} over the pipe.", nodes.Count, pid);
		return new LiveXamlTree { Nodes = nodes, Channel = PipeChannel };
	}

	/// <summary>
	/// Reads one element's properties by injecting the provider with a properties request. By default
	/// only set (non-default) properties come back; <paramref name="includeDefaults"/> asks for the
	/// framework defaults too. Returns a result with a detail (and no properties) rather than throwing
	/// when the element cannot be read.
	/// </summary>
	public LiveXamlProperties ReadProperties(int pid, ulong handle, bool includeDefaults)
	{
		lock (_requests) return ReadPropertiesCore(pid, handle, includeDefaults);
	}

	private LiveXamlProperties ReadPropertiesCore(int pid, ulong handle, bool includeDefaults)
	{
		var request = includeDefaults ? $"properties {handle} all" : $"properties {handle}";

		var (pipe, unready) = _provider.Connect(pid);
		if (pipe is null) return new LiveXamlProperties { Handle = handle, Detail = unready };

		// The reply is a status line and then the rows, so "the chain could not be read" stays
		// distinguishable from "read it and there was nothing" -- a distinction an empty reply cannot
		// make at all.
		var served = pipe.Request(request, Reply);
		if (served is not null)
		{
			var lines = served.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			if (lines.Length > 0 && lines[0] == "error")
			{
				return new LiveXamlProperties
				{
					Handle = handle,
					Detail = "The XAML provider could not read that element's property chain. The handle may "
						+ "name something that is not an element, or something no longer in the tree.",
				};
			}

			if (lines.Length > 0 && lines[0] == "ok")
			{
				var fromPipe = XamlProviderWire.ParseProperties(lines.Skip(1), handle);
				_logger.LogInformation(
					"Read {Count} propert(y/ies) for handle {Handle} from pid {Pid} over the pipe.", fromPipe.Count, handle, pid);
				return fromPipe;
			}
		}

		return new LiveXamlProperties { Handle = handle, Detail = Unanswered(request, $"the properties of handle {handle}") };
	}

	/// <summary>
	/// Arms one of the overlay's pointer modes (#18, #19) from this side. The in-app toolbar is already
	/// resident -- any XAML tool installs it -- so this is the same act as pressing its Select Element
	/// or Rulers button, and exists because an agent chasing a visual path to an element should not
	/// have to ask a person to press one. Confirms the overlay actually armed rather than assuming it.
	/// </summary>
	public LiveXamlSelection EnterSelectMode(int pid, bool includeAllElements, bool justMyXaml, string mode)
	{
		lock (_requests) return EnterSelectModeCore(pid, includeAllElements, justMyXaml, mode);
	}

	/// <summary>
	/// Disarms select mode, the same act as the toolbar's Idle button.
	/// <para>
	/// The other half of a switch that only had one position reachable from here. Arming puts a
	/// pointer-capturing layer over the app and waits for a click; nothing but that click, or a person
	/// pressing Idle, took it away again -- selecting by handle does not, because it never goes through
	/// the click path that ends the mode. An agent that armed and then changed its mind had left the
	/// app modal with no way back.
	/// </para>
	/// <para>
	/// Clearing the pick stays a separate act. Armed and picked are two pieces of state, and folding
	/// them together would remove "clear this one and let me pick another", which is the ordinary way
	/// a person uses the toolbar.
	/// </para>
	/// </summary>
	public LiveXamlSelection ExitSelectMode(int pid)
	{
		lock (_requests) return ExitSelectModeCore(pid);
	}

	private LiveXamlSelection ExitSelectModeCore(int pid)
	{
		var (pipe, unready) = _provider.Connect(pid);
		if (pipe is null) return new LiveXamlSelection { Detail = unready };

		var request = "idle";

		if (pipe.Request(request, Reply) is null)
		{
			return new LiveXamlSelection { Detail = Unanswered(request, "the overlay to stop capturing the pointer") };
		}

		// Answered from the provider's own state rather than from the fact that it acknowledged, for
		// the same reason arming is: a later rose_xaml_selection reads that state, and a reply that
		// merely echoed the request could contradict it with nothing to say which was right.
		var after = ReadSelectionCore();
		return after with
		{
			Detail = after.Armed
				? $"The overlay is still in {after.Mode} mode."
				: "The overlay is idle; the app takes its own clicks again.",
		};
	}
	private LiveXamlSelection EnterSelectModeCore(int pid, bool includeAllElements, bool justMyXaml, string mode)
	{
		// Both modes lay the same pointer-capturing layer over the app, so both are armed by the same
		// verb with the mode as one more token. Tokens rather than flags in the name: the provider
		// parses them, and a request that does not mention a toggle leaves whatever the person set on
		// the toolbar alone.
		var rulers = string.Equals(mode, "rulers", StringComparison.OrdinalIgnoreCase);
		var request = "select"
			+ (rulers ? " rulers" : string.Empty)
			+ (includeAllElements ? " all" : string.Empty)
			+ (justMyXaml ? " myxaml" : " nomyxaml");

		var (pipe, unready) = _provider.Connect(pid);
		if (pipe is null) return new LiveXamlSelection { Detail = unready };

		// The provider reports the extent XAML arranged its capture layer at, and a zero is checked
		// rather than assumed: an overlay that exists but was given no area is armed, invisible, and
		// cannot be clicked -- which is indistinguishable from working if all you check is that it
		// armed. That exact state shipped once, so it is now a reported failure.
		//
		// The provider waits for the layout pass before answering, so the extent in the reply is the
		// arranged one rather than whatever it was before XAML got to it.
		var served = pipe.Request(request, Reply);
		if (served is null)
		{
			return new LiveXamlSelection { Detail = Unanswered(request, $"{(rulers ? "rulers" : "select")} mode to be armed") };
		}

		var fields = XamlWire.Fields(served.Trim());
		var width = fields.Length > 1 && int.TryParse(fields[1], out var armedWidth) ? armedWidth : 0;
		var height = fields.Length > 2 && int.TryParse(fields[2], out var armedHeight) ? armedHeight : 0;
		if (width <= 0 || height <= 0)
		{
			return new LiveXamlSelection
			{
				Detail = $"The mode armed but its overlay was arranged at {width}x{height}, so nothing can be "
					+ "pointed at. The app's diagnostics UI layer gave the overlay no area.",
			};
		}

		// Reported from what the provider recorded, not from what was asked for. The two agree
		// whenever the round trip worked, and the point is what happens when they do not: a later
		// rose_xaml_selection reads the provider's own state, so an arming response that merely echoed
		// the request would contradict that read with nothing to say which of them was right.
		// Answering from the recorded value makes the two agree by construction.
		var (recordedMode, recorded, known) = OverlayState();
		var wanted = rulers ? "rulers" : "select";
		if (!known || recordedMode != wanted)
		{
			return new LiveXamlSelection
			{
				Mode = known ? recordedMode : "idle",
				Armed = known && recordedMode != "idle",
				Detail = $"{wanted} mode was armed, but the toolbar has not confirmed it, so what it is "
					+ "filtering cannot be reported. Read the selection again in a moment.",
			};
		}

		return new LiveXamlSelection
		{
			Armed = true,
			Mode = recordedMode,
			JustMyXaml = recorded,
			Detail = rulers
				? "Rulers mode is armed: the picked element shows its margin and padding, and whatever "
					+ "the pointer is over is measured against it. Clicking anchors somewhere else."
				: "Select mode is armed: click an element in the app, then read the selection.",
		};
	}

	/// <summary>
	/// Clears the picked element: the mark drawn over the app and the record on disk, together.
	/// <para>
	/// Both halves or it is a lie. Hiding the outline alone leaves <c>rose_xaml_selection</c> naming an
	/// element the person can no longer see; deleting the files alone leaves a mark over an app that no
	/// longer means anything. The provider does both and then confirms, so this can report which of
	/// "cleared" and "there was nothing selected" actually happened rather than treating them as one.
	/// </para>
	/// </summary>
	public LiveXamlSelection ClearSelection(int pid)
	{
		lock (_requests) return ClearSelectionCore(pid);
	}

	private LiveXamlSelection ClearSelectionCore(int pid)
	{
		var (pipe, unready) = _provider.Connect(pid);
		if (pipe is null) return new LiveXamlSelection { Detail = unready };

		var request = "deselect";

		var served = pipe.Request(request, Reply);
		if (served is null)
		{
			return new LiveXamlSelection { Detail = Unanswered(request, "the deselect to be confirmed") };
		}

		var had = served.Trim() == "cleared";

		var (mode, justMyXaml, _) = OverlayState();

		return new LiveXamlSelection
		{
			Armed = mode != "idle",
			Mode = mode,
			JustMyXaml = justMyXaml,
			Detail = had
				? "The selection was cleared; the outline over the app is gone."
				: "Nothing was selected, so there was nothing to clear.",
		};
	}

	/// <summary>
	/// Selects the element a handle names, with no hit test involved.
	/// <para>
	/// The reason this exists is that some controls cannot be clicked on. A slider is the reported
	/// case, and it is not fixable where the click lands: what a click resolves to is the framework's
	/// answer, and it is sometimes not the element anybody meant -- Visual Studio's own XAML tools
	/// have the same gap. Arriving from <c>rose_xaml_tree</c>, which already hands out a handle for
	/// every element, reaches them.
	/// </para>
	/// <para>
	/// It is also how an agent selects structurally -- by type, by name, by the file the markup came
	/// from -- rather than asking a person to point at something.
	/// </para>
	/// </summary>
	public LiveXamlSelection SelectByHandle(int pid, ulong handle)
	{
		lock (_requests) return SelectByHandleCore(pid, handle);
	}

	private LiveXamlSelection SelectByHandleCore(int pid, ulong handle)
	{
		var (pipe, unready) = _provider.Connect(pid);
		if (pipe is null) return new LiveXamlSelection { Detail = unready };

		// A refusal costs what an answer costs, which a channel of files could not manage: the provider
		// writes a selection only when it has one to record, so a handle naming something that is not an
		// element produced no file and the refusal arrived as a timeout. A reply always arrives.
		var request = $"selecthandle {handle}";

		var served = pipe.Request(request, Reply);
		if (served is null)
		{
			return new LiveXamlSelection { Detail = Unanswered(request, $"an answer about handle {handle}") };
		}

		if (served.Trim() != "selected")
		{
			return new LiveXamlSelection
			{
				Detail = $"Handle {handle} was not selected. It may name something that is not an element, or "
					+ "something no longer in the tree; rose_xaml_tree lists what is.",
			};
		}

		return ReadSelectionCore();
	}

	/// <summary>
	/// Loads the provider and puts the in-app toolbar up, without asking it anything. Returns null when
	/// it is there, or the sentence saying why it is not.
	/// </summary>
	/// <remarks>
	/// The toolbar is for the person at the app, and it is worth having whether or not an agent ever
	/// asks a XAML question. Waiting for the first <c>rose_xaml_*</c> call to install it makes a tool
	/// for a human depend on a machine having had the thought first.
	/// <para>
	/// Everything else here loads the provider as a side effect of needing it. This is the same load,
	/// asked for on its own, so a caller that wants the toolbar early does not have to invent a
	/// question to get it.
	/// </para>
	/// </remarks>
	public string? AttachTooling(int pid)
	{
		lock (_requests) return _provider.Connect(pid).Unready;
	}

	/// <summary>
	/// Reads the element that was picked, if any. Deliberately does not inject: the toolbar is resident
	/// and owns the selection, and the person may have picked without this side being involved at all --
	/// which is the case this exists for. That is also why the mode is asked of the provider rather than
	/// remembered here.
	/// </summary>
	public LiveXamlSelection ReadSelection()
	{
		lock (_requests) return ReadSelectionCore();
	}

	private LiveXamlSelection ReadSelectionCore()
	{
		if (_provider.ResidentPipe is null)
		{
			return new LiveXamlSelection { Detail = "No XAML tool has run against this session yet, so the in-app toolbar is not installed." };
		}

		// This call deliberately does not inject, and over the pipe that costs nothing: the provider
		// answers from what it holds, and the reply is matched to this request by the id it echoes.
		var report = SelectionOverPipe();
		if (report is null) return new LiveXamlSelection { Detail = Unanswered("selection", "what is selected") };

		var (mode, justMyXaml, rows, gone) = (report.Mode, report.JustMyXaml, report.Rows, report.Gone);

		// Any mode that is not idle has a layer over the app collecting the pointer, which is what a
		// caller asking whether it is armed wants to know. Testing for select alone would report an
		// app that cannot be clicked as one that can.
		var armed = mode != "idle";
		if (rows.Count == 0)
		{
			// A selection that went away on its own says why. Without this the answer is "nothing has been
			// picked yet", which is true and useless: something *was* picked, the app took it away, and the
			// caller is left wondering whether their select ever worked.
			return new LiveXamlSelection
			{
				Armed = armed,
				Mode = mode,
				JustMyXaml = justMyXaml,
				Detail = gone.Length > 0
					? gone
					: armed
						? $"The overlay is in {mode} mode; nothing has been picked yet."
						: "Nothing has been picked yet. Press Select Element on the in-app toolbar, or arm it from here.",
			};
		}

		try
		{
			// Every row is a candidate, topmost first, and the first is the pick. The stack is read
			// whole because one element is rarely the one wanted: a click on a button lands on part of
			// its template, and a click meant for a container lands on the content inside it.
			var candidates = new List<LiveXamlSelectionCandidate>();
			var byHandle = ReadTreeIndex();

			foreach (var line in rows)
			{
				var fields = XamlWire.Fields(line);
				if (fields.Length < 4 || !ulong.TryParse(fields[0], out var handle)) continue;

				var name = fields[2];
				byHandle.TryGetValue(handle, out var node);

				candidates.Add(new LiveXamlSelectionCandidate
				{
					Handle = handle,
					TypeName = fields[1],
					Name = string.IsNullOrEmpty(name) ? null : name,
					IsFrameworkType = fields[3] == "1",

					// Joined from the tree rather than repeated in the selection file: the provider
					// reports an element's source info once, when it enumerates, and its address is
					// computed from the same parent and sibling relations -- so the tree snapshot is
					// the one place either of them exists, and copying them here could only drift.
					File = node?.File,
					Line = node?.Line,
					Address = node?.Address,
				});
			}

			if (candidates.Count == 0)
			{
				return new LiveXamlSelection
				{
					Armed = armed,
					Mode = mode,
					JustMyXaml = justMyXaml,
					Detail = "The recorded selection could not be read."
				};
			}

			var picked = candidates[0];
			return new LiveXamlSelection
			{
				Selected = true,
				Armed = armed,
				Mode = mode,
				JustMyXaml = justMyXaml,
				Handle = picked.Handle,
				TypeName = XamlProviderWire.EmptyToNull(picked.TypeName),
				Name = picked.Name,
				Address = picked.Address,
				Candidates = candidates,
			};
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Reading the XAML selection failed.");
			return new LiveXamlSelection
			{
				Armed = armed,
				Mode = mode,
				JustMyXaml = justMyXaml,
				Detail = $"Could not read the selection: {exception.Message}",
			};
		}
	}

	/// <summary>
	/// The last tree snapshot indexed by handle, for joining source info onto a selection. Empty when
	/// no tree has been read: a selection is still perfectly usable without it, so a missing snapshot
	/// costs the file and line rather than the answer.
	/// </summary>
	private Dictionary<ulong, LiveXamlNode> ReadTreeIndex()
	{
		try
		{
			// The same tree every other read asks for, and over the same channel. It is joined onto the
			// selection for source info and addresses, which exist in the tree and nowhere else.
			if (_provider.ResidentPipe is not { } pipe) return [];

			var served = pipe.Request("tree", Reply);
			if (served is null) return [];

			return XamlProviderWire.ParseTree(served.Split('\n', StringSplitOptions.RemoveEmptyEntries))
				.ToDictionary(node => node.Handle);
		}
		catch (ArgumentException)
		{
			return [];
		}
	}

	/// <summary>
	/// What the in-app toolbar says its mode is. Absent or unreadable counts as idle: the file is only
	/// ever a hint about a UI the person controls, and no tool should fail because it is missing.
	/// </summary>
	/// <summary>
	/// What the overlay is doing, asked of the provider.
	/// </summary>
	/// <remarks>
	/// The provider's own report, never what this side last asked for. The person can change the mode
	/// from the toolbar without the host being in the conversation at all, so a reply that echoed the
	/// request would contradict the next read with nothing to say which was right.
	/// <para>
	/// Known, unless the provider did not answer. The reply is this request's answer because it echoes
	/// the request's id, which is also what keeps a reply to a request the host gave up on from being
	/// read as this one.
	/// </para>
	/// </remarks>
	private (string Mode, bool JustMyXaml, bool Known) OverlayState()
	{
		var report = SelectionOverPipe();
		return report is not null ? (report.Mode, report.JustMyXaml, true) : ("idle", true, false);
	}

	/// <summary>
	/// Everything the overlay knows about the pick, in one exchange: the mode, whether it is filtering to
	/// the app's own markup, why the last selection went away, and the rows behind the current one.
	/// Null when there is no pipe or it did not answer, which the caller reports as a selection it could
	/// not read.
	/// </summary>
	/// <remarks>
	/// One request rather than one per fact, because the facts have to agree. Read as three exchanges, a
	/// mode from one and rows from another can describe a state that never existed at any instant. A
	/// single frame is consistent by construction.
	/// </remarks>
	private OverlayReport? SelectionOverPipe()
	{
		if (_provider.ResidentPipe is not { } pipe) return null;

		var served = pipe.Request("selection", Reply);
		if (served is null) return null;

		var lines = served.Split('\n');
		var header = XamlWire.Fields(lines[0]);
		if (header.Length < 3) return null;

		return new OverlayReport(
			header[0],
			header[1] == "1",
			header[2],
			[.. lines.Skip(1).Where(line => line.Length > 0)]);
	}

	/// The overlay's answer about the pick, as one reply rather than three files.
	private sealed record OverlayReport(string Mode, bool JustMyXaml, string Gone, List<string> Rows);

	/// <summary>
	/// Live-edits the target by diffing two XAML versions and applying the edits to its visual tree (#12).
	/// Property changes, removals and additions all apply, and on any element the diff can address rather
	/// than only a named one. Returns each computed edit with its outcome, plus the diff engine's notes.
	/// <para>
	/// The old version is usually not passed at all. A caller in the edit-to-live loop names the file it
	/// has just written and this side diffs against what it last sent to the app -- see <see
	/// cref="XamlApplyBaseline"/> for why that state belongs on this end.
	/// </para>
	/// </summary>
	public LiveXamlApplyResult ApplyEdits(int pid, string? oldXaml, string? newXaml, string? filePath)
	{
		lock (_requests) return _apply.ApplyEditsCore(pid, oldXaml, newXaml, filePath);
	}

	private string Unanswered(string request, string what) => XamlChannelBounds.Unanswered(request, what, Reply);

	/// <summary>
	/// Asks the resident provider to give back the two framework interfaces it holds.
	/// <para>
	/// Called on detach rather than only on disposal, and the difference is what makes it observable:
	/// disposal happens as the host shuts down, after its client has closed the stdin that carried its
	/// log, so the one line saying whether the release happened is written where nothing is left to
	/// read it.
	/// </para>
	/// </summary>
	public void EndProviderSession()
	{
		lock (_requests) _provider.Release();
	}

	/// <summary>Ends the provider session and deletes the sandbox folder it staged.</summary>
	public void Dispose()
	{
		// Under the same lock as a request, or the folder can be deleted from under one in flight.
		lock (_requests) _provider.Dispose();
	}
}
