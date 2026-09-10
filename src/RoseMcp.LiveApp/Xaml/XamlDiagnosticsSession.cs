using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.XamlDiff;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Injects a XAML diagnostics provider into the target and reads back what it reports.
/// <c>InitializeXamlDiagnosticsEx</c> loads the provider into the app by pid, out of a working folder
/// this side stages; the two ends then talk over a named pipe the provider connects back on. The
/// provider must match the target's architecture, which is this host's architecture -- an x64 provider
/// for a classic UWP app emulated on ARM64.
/// <para>
/// Which provider, which library exports the initialiser, which class id, and whether that folder
/// needs AppContainer grants are all asked of the target rather than assumed. Four separate
/// hard-codings of UWP cost not that WinUI 3 failed -- it is that it failed after a twenty-second
/// wait, blaming the app for not being packaged. <see cref="XamlStackProbe"/> reads the framework
/// DLLs the process has loaded and <see cref="XamlTaps"/> maps the answer to a tap, so a stack with
/// no provider is refused immediately and by name.
/// </para>
/// <para>
/// One request at a time, and the lock is re-entrant. The host serves MCP calls concurrently while
/// every XAML request shares one pipe, which carries one request and one reply at a time, and two at
/// once produced a tree of 22 elements where the app has 24 -- a truncated tree handing out handles
/// for a tree that is not there. Re-entrant because selecting by handle finishes by reading the
/// selection, which takes the lock again on the same thread.
/// </para>
/// </summary>
internal sealed class XamlDiagnosticsSession(ILogger logger) : IDisposable
{
	// HRESULT_FROM_WIN32(ERROR_NOT_FOUND): the well-known diagnostics endpoint is not there yet.
	private const int ErrorNotFound = unchecked((int)0x80070490);

	// What a tree read reports about which channel answered. Named constants rather than literals at
	// the two return sites, because the whole value of the field is that a test can tell the two
	// apart, and a test comparing against a literal spelled differently in one place would pass
	// while reporting the wrong channel.
	private const string PipeChannel = "pipe";


	// Every wait on the provider, and the sentence each produces when it expires. Long enough for a
	// XAML app to get its first tree up, short enough that a target which genuinely has no XAML UI
	// does not hold a tool call for an uncomfortable length of time -- and bounded without exception,
	// because a wait with no bound here is a tool call that never returns rather than a slow one.
	private readonly XamlChannelBounds _bounds = XamlChannelBounds.FromEnvironment();

	private string? _workDir;
	private string? _stagedProvider;

	// Which XAML framework the target turned out to be, and the tap serving it. Resolved once and
	// kept: a session has exactly one target process, so the stack cannot change underneath it, and
	// re-reading the module list on every request would pay for an answer that cannot have moved.
	private XamlStackDetection? _stack;

	private XamlTap? _tap;

	private InitializeXamlDiagnosticsEx? _initialise;

	// The XAML framework dll the initialiser is pointed at, resolved from the target, or null where
	// the framework exports its own initialiser and needs no telling.
	private string? _diagnosticsPath;
	// The host end of the pipe the provider connects back on, and the only way a request reaches it.
	// A pipe an AppContainer cannot reach is a session with no XAML in it, not a slower one.
	private XamlProviderPipe? _pipe;

	// How many times this session has loaded the provider. One is the intent and the ordinary case;
	// anything more means the pipe dropped and the channel was rebuilt.
	private int _injections;


	// Whether the target's diagnostics endpoint has ever answered this session. It separates two
	// failures that share an HRESULT and mean opposite things: ERROR_NOT_FOUND before any read is an
	// app whose tree is not up yet, or one with no XAML at all, and waiting is the advice. The same
	// code after a read has succeeded is an app that was serving us and has stopped, where waiting is
	// exactly the wrong advice -- it was reported costing an hour of looking at the wrong app, because
	// the message offered "still starting" and "no XAML UI" and neither had been true for some time.
	private bool _endpointAnswered;

	// What this side has already sent to the app, per source file (#12). It is held here rather than by
	// the caller for two reasons: this is the only place that can tell whether an apply reached the
	// provider, and a caller that has just written a file no longer holds what was there before.
	private readonly XamlApplyBaseline _baselines = new();

	// One request at a time, and this is measured rather than defensive (#93). The host serves MCP
	// calls concurrently -- two tree reads issued together finish in the time of one, where serialised
	// they would take twice as long -- and everything below shares one pipe, which carries one request
	// and one reply at a time.
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
	// Every public entry point takes it once and calls a Core method that assumes it is held, so no
	// path takes it twice and its re-entrancy is not relied on. That is worth keeping rather than
	// merely true: a Core method that takes the lock itself would deadlock under a SemaphoreSlim and
	// pass under this one, so the pairing is what makes the choice of lock free rather than load-bearing.
	private readonly Lock _requests = new();

	/// <summary>
	/// Reads a snapshot of the target's live visual tree, injecting the provider first. Returns a tree
	/// with a <see cref="LiveXamlTree.Detail"/> and no nodes -- never throws -- when the provider is not
	/// available for this architecture, injection fails, or the target has no XAML UI.
	/// </summary>
	public LiveXamlTree ReadTree(int pid)
	{
		lock (_requests) return ReadTreeCore(pid);
	}

	private LiveXamlTree ReadTreeCore(int pid)
	{
		var unready = EnsureProvider(pid);
		if (unready is not null) return new LiveXamlTree { Detail = unready };

		var served = _pipe!.Request("tree", _bounds.Snapshot);
		if (served is null) return new LiveXamlTree { Detail = Unanswered("a tree") };

		var nodes = ParseTree(served.Split('\n', StringSplitOptions.RemoveEmptyEntries));
		logger.LogInformation("Read a XAML tree of {Count} element(s) from pid {Pid} over the pipe.", nodes.Count, pid);
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

		var unready = EnsureProvider(pid);
		if (unready is not null) return new LiveXamlProperties { Handle = handle, Detail = unready };

		// The reply is a status line and then the rows, so "the chain could not be read" stays
		// distinguishable from "read it and there was nothing" -- a distinction an empty reply cannot
		// make at all.
		var served = _pipe!.Request(request, _bounds.Snapshot);
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
				var fromPipe = ParseProperties(lines.Skip(1), handle);
				logger.LogInformation(
					"Read {Count} propert(y/ies) for handle {Handle} from pid {Pid} over the pipe.", fromPipe.Count, handle, pid);
				return fromPipe;
			}
		}

		return new LiveXamlProperties { Handle = handle, Detail = Unanswered($"the properties of handle {handle}") };
	}

	/// <summary>
	/// Arms select mode (#18) from this side. The in-app toolbar is already resident -- any XAML tool
	/// installs it -- so this is the same act as pressing its Select Element button, and exists because
	/// an agent chasing a visual path to an element should not have to ask a person to press it.
	/// Confirms the overlay actually armed rather than assuming it.
	/// </summary>
	public LiveXamlSelection EnterSelectMode(int pid, bool includeAllElements, bool justMyXaml)
	{
		lock (_requests) return EnterSelectModeCore(pid, includeAllElements, justMyXaml);
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
		var unready = EnsureProvider(pid);
		if (unready is not null) return new LiveXamlSelection { Detail = unready };

		if (_pipe!.Request("idle", _bounds.Snapshot) is null)
		{
			return new LiveXamlSelection { Detail = Unanswered("select mode to be disarmed") };
		}

		// Answered from the provider's own state rather than from the fact that it acknowledged, for
		// the same reason arming is: a later rose_xaml_selection reads that state file, and a reply
		// that merely echoed the request could contradict it with nothing to say which was right.
		var after = ReadSelectionCore();
		return after with { Detail = after.Armed ? "Select mode is still armed." : "Select mode is off." };
	}
	private LiveXamlSelection EnterSelectModeCore(int pid, bool includeAllElements, bool justMyXaml)
	{
		// Tokens rather than flags in the name: the provider parses them, and a request that does not
		// mention a toggle leaves whatever the person set on the toolbar alone.
		var request = "select"
			+ (includeAllElements ? " all" : string.Empty)
			+ (justMyXaml ? " myxaml" : " nomyxaml");

		var unready = EnsureProvider(pid);
		if (unready is not null) return new LiveXamlSelection { Detail = unready };

		// The provider reports the extent XAML arranged its capture layer at, and a zero is checked
		// rather than assumed: an overlay that exists but was given no area is armed, invisible, and
		// cannot be clicked -- which is indistinguishable from working if all you check is that it
		// armed. That exact state shipped once, so it is now a reported failure.
		//
		// The provider waits for the layout pass before answering, so the extent in the reply is the
		// arranged one rather than whatever it was before XAML got to it.
		var served = _pipe!.Request(request, _bounds.Snapshot);
		if (served is null)
		{
			return new LiveXamlSelection { Detail = Unanswered("select mode to be armed") };
		}

		var fields = served.Trim().Split('\t');
		var width = fields.Length > 1 && int.TryParse(fields[1], out var armedWidth) ? armedWidth : 0;
		var height = fields.Length > 2 && int.TryParse(fields[2], out var armedHeight) ? armedHeight : 0;
		if (width <= 0 || height <= 0)
		{
			return new LiveXamlSelection
			{
				Detail = $"Select mode armed but its overlay was arranged at {width}x{height}, so nothing can be picked. "
					+ "The app's diagnostics UI layer gave the overlay no area.",
			};
		}

		// Reported from what the provider recorded, not from what was asked for. The two agree
		// whenever the round trip worked, and the point is what happens when they do not: a later
		// rose_xaml_selection reads the provider's own state file, so an arming response that merely
		// echoed the request would contradict that read with nothing to say which of them was right.
		// Answering from the recorded value makes the two agree by construction.
		var (mode, recorded, known) = OverlayState();
		if (!known || mode != "select")
		{
			return new LiveXamlSelection
			{
				Detail = "Select mode was armed, but the toolbar has not confirmed it, so what it is filtering "
					+ "cannot be reported. Read the selection again in a moment.",
			};
		}

		return new LiveXamlSelection
		{
			Armed = true,
			JustMyXaml = recorded,
			Detail = "Select mode is armed: click an element in the app, then read the selection.",
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
		var unready = EnsureProvider(pid);
		if (unready is not null) return new LiveXamlSelection { Detail = unready };

		var served = _pipe!.Request("deselect", _bounds.Snapshot);
		if (served is null)
		{
			return new LiveXamlSelection { Detail = Unanswered("the deselect to be confirmed") };
		}

		var had = served.Trim() == "cleared";

		var (mode, justMyXaml, _) = OverlayState();

		return new LiveXamlSelection
		{
			Armed = mode == "select",
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
		var unready = EnsureProvider(pid);
		if (unready is not null) return new LiveXamlSelection { Detail = unready };

		// A refusal costs what an answer costs, which a channel of files could not manage: the provider
		// writes a selection only when it has one to record, so a handle naming something that is not an
		// element produced no file and the refusal arrived as a timeout. A reply always arrives.
		var served = _pipe!.Request($"selecthandle {handle}", _bounds.Snapshot);
		if (served is null)
		{
			return new LiveXamlSelection { Detail = Unanswered($"an answer about handle {handle}") };
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
		lock (_requests) return EnsureProvider(pid);
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
		if (_pipe?.Connected != true)
		{
			return new LiveXamlSelection { Detail = "No XAML tool has run against this session yet, so the in-app toolbar is not installed." };
		}

		// This call deliberately does not inject, and over the pipe that costs nothing: the provider
		// answers from what it holds, and a reply read from the pipe the request went out on is this
		// request's answer by construction.
		var report = SelectionOverPipe();
		if (report is null) return new LiveXamlSelection { Detail = Unanswered("what is selected") };

		var (mode, justMyXaml, rows, gone) = (report.Mode, report.JustMyXaml, report.Rows, report.Gone);

		var armed = mode == "select";
		if (rows.Count == 0)
		{
			// A selection that went away on its own says why. Without this the answer is "nothing has been
			// picked yet", which is true and useless: something *was* picked, the app took it away, and the
			// caller is left wondering whether their select ever worked.
			return new LiveXamlSelection
			{
				Armed = armed,
				JustMyXaml = justMyXaml,
				Detail = gone.Length > 0
					? gone
					: armed
						? "Select mode is armed; nothing has been picked yet."
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
				var fields = line.Split('\t');
				if (fields.Length < 3 || !ulong.TryParse(fields[0], out var handle)) continue;

				var name = Unescape(fields[2]);
				byHandle.TryGetValue(handle, out var node);

				candidates.Add(new LiveXamlSelectionCandidate
				{
					Handle = handle,
					TypeName = Unescape(fields[1]),
					Name = string.IsNullOrEmpty(name) ? null : name,
					IsFrameworkType = fields.Length > 3 && fields[3] == "1",

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
					JustMyXaml = justMyXaml,
					Detail = "The recorded selection could not be read."
				};
			}

			var picked = candidates[0];
			return new LiveXamlSelection
			{
				Selected = true,
				Armed = armed,
				JustMyXaml = justMyXaml,
				Handle = picked.Handle,
				TypeName = EmptyToNull(picked.TypeName),
				Name = picked.Name,
				Address = picked.Address,
				Candidates = candidates,
			};
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Reading the XAML selection failed.");
			return new LiveXamlSelection
			{
				Armed = armed,
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
			if (_pipe?.Connected != true) return [];

			var served = _pipe.Request("tree", _bounds.Snapshot);
			if (served is null) return [];

			return ParseTree(served.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
	/// Known, unless the provider did not answer. It needs no generation stamp: a reply read from the
	/// pipe the request went out on is this request's answer by construction, which is what a file left
	/// in a folder can never be.
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
		if (_pipe?.Connected != true) return null;

		var served = _pipe.Request("selection", _bounds.Snapshot);
		if (served is null) return null;

		var lines = served.Split('\n');
		var header = lines[0].Split('\t');
		if (header.Length < 2) return null;

		return new OverlayReport(
			header[0],
			header[1] == "1",
			header.Length > 2 ? Unescape(header[2]) : string.Empty,
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
		lock (_requests) return ApplyEditsCore(pid, oldXaml, newXaml, filePath);
	}

	private LiveXamlApplyResult ApplyEditsCore(int pid, string? oldXaml, string? newXaml, string? filePath)
	{
		var (inputs, failure) = Resolve(pid, oldXaml, newXaml, filePath);
		if (failure is not null) return new LiveXamlApplyResult { Detail = failure };

		// Nothing to diff against, which is not a failure: the file's contents are the baseline from
		// here on, so the caller's next edit applies on its own. The note says which reason it was.
		if (inputs!.OldXaml is null)
		{
			return new LiveXamlApplyResult { Notes = inputs.Note is null ? [] : [inputs.Note] };
		}

		XamlDiffResult diff;
		try
		{
			diff = RoseMcp.XamlDiff.XamlDiff.Compute(inputs.OldXaml, inputs.NewXaml);
		}
		catch (Exception exception)
		{
			return new LiveXamlApplyResult { Detail = $"Could not diff the XAML: {exception.Message}" };
		}

		// The target goes to the provider exactly as the diff wrote it, `#name` or path alike: whether an
		// address resolves is a question about the live tree, so it is asked where the live tree is.
		//
		// An addition is the one edit that is not a single command. There is no way to apply markup --
		// CreateInstance builds one object from a type name -- so the subtree is taken apart into build
		// steps and sent as several commands, and this edit's outcome is the outcome of all of them. The
		// taking apart lives in the diff library, which is pure and unit tested; doing it here would put
		// the fiddliest part of this somewhere no unit test can reach.
		var commands = new List<string>();
		var plans = new List<(XamlEdit Edit, List<string> Keys)>();
		var notes = new List<string>();
		if (inputs.Note is not null) notes.Add(inputs.Note);
		notes.AddRange(diff.Notes);

		foreach (var edit in diff.Edits)
		{
			var keys = new List<string>();

			if (edit.Kind is XamlEditKind.SetProperty or XamlEditKind.ClearProperty or XamlEditKind.RemoveChild)
			{
				var property = edit.Property ?? string.Empty;
				commands.Add(Line(Op(edit.Kind), edit.Target, property, edit.ValueType ?? string.Empty, edit.Value ?? string.Empty, string.Empty, 0));
				keys.Add(Key(Op(edit.Kind), edit.Target, property, string.Empty));
			}
			else if (edit.Kind is XamlEditKind.AddChild && edit.Payload is { } payload)
			{
				try
				{
					foreach (var step in XamlMaterialiser.Steps(payload, edit.Target, edit.Index ?? 0))
					{
						var (line, key) = Command(step);
						commands.Add(line);
						keys.Add(key);
					}
				}
				catch (Exception exception)
				{
					keys.Clear();
					notes.Add($"The element added under {edit.Target} could not be taken apart into build steps: {exception.Message}");
				}
			}
			else if (edit.Kind is XamlEditKind.SetResource && edit.Payload is { } resource)
			{
				// A resource is built the same way an added element is and then put somewhere else:
				// behind a key rather than into a parent's children. So the same steps run, minus the
				// attach, and one ReplaceResource finishes it.
				try
				{
					foreach (var step in XamlMaterialiser.Unattached(resource))
					{
						var (line, key) = Command(step);
						commands.Add(line);
						keys.Add(key);
					}

					var name = edit.Property ?? string.Empty;
					commands.Add(Line("ReplaceResource", edit.Target, name, string.Empty, string.Empty, XamlMaterialiser.RootSlot, 0));
					keys.Add(Key("ReplaceResource", edit.Target, name, XamlMaterialiser.RootSlot));
				}
				catch (Exception exception)
				{
					keys.Clear();
					notes.Add($"The resource '{edit.Property}' on {edit.Target} could not be taken apart into build steps: {exception.Message}");
				}
			}

			plans.Add((edit, keys));
		}

		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		if (commands.Count > 0)
		{
			var unready = EnsureProvider(pid);
			if (unready is not null) return new LiveXamlApplyResult { Detail = unready };

			var served = _pipe!.Request("apply\n" + string.Join("\n", commands), _bounds.Snapshot);
			if (served is null)
			{
				// Not retried anywhere, and the baseline is deliberately left where it was. A structural
				// edit is not idempotent, and a missing reply cannot tell "never ran" from "ran, and the
				// answer was lost" -- so resending would put a second copy of everything this batch adds
				// into the app. The message says what that costs the caller.
				return new LiveXamlApplyResult
				{
					Detail = Unanswered("a batch of edits to be applied")
						+ " The edits may or may not have reached the app, so applying the same change again could "
						+ "add a second copy of anything this one was adding.",
				};
			}

			statuses = ParseApplyResults(served.Split('\n', StringSplitOptions.RemoveEmptyEntries));
			logger.LogInformation("Applied {Count} XAML command(s) to pid {Pid} over the pipe.", commands.Count, pid);
		}

		// Advanced whether or not every edit took, and that is the deliberate half. The app has been
		// sent this version; re-sending a structural edit because something else in the batch failed
		// would duplicate the elements that did go in. The failures are in the results to act on.
		if (inputs.SourcePath is not null) _baselines.Advance(inputs.SourcePath, inputs.NewXaml);

		var results = new List<LiveXamlEditResult>();
		foreach (var (edit, keys) in plans)
		{
			results.Add(new LiveXamlEditResult
			{
				Kind = edit.Kind.ToString(),
				Target = edit.Target,
				Property = edit.Property,
				Value = edit.Value,
				Status = Outcome(keys, statuses),
			});
		}

		return new LiveXamlApplyResult
		{
			Applied = results.Count(result => result.Status == "applied"),
			Results = results,
			Notes = notes,
		};
	}

	/// <summary>
	/// Works out what to diff, from what the caller gave, or names what is missing or contradictory.
	/// <para>
	/// There are three ways to ask, and the one this was built for is to name the file and nothing
	/// else. Two versions of the markup is the original shape, still honoured because a caller
	/// composing markup may have no file at all. A file plus an explicit old version is the escape
	/// hatch for the first apply after an edit this side never saw.
	/// </para>
	/// <para>
	/// A file plus a new version is refused rather than reconciled. Two answers to "what does it say
	/// now" is a question, and picking one silently is how a tool applies something convincingly and
	/// not what was asked.
	/// </para>
	/// </summary>
	private (ApplyInputs? Inputs, string? Error) Resolve(int pid, string? oldXaml, string? newXaml, string? filePath)
	{
		var hasFile = !string.IsNullOrWhiteSpace(filePath);
		var hasNew = !string.IsNullOrEmpty(newXaml);
		var hasOld = !string.IsNullOrEmpty(oldXaml);

		if (!hasFile)
		{
			if (!hasNew)
			{
				return (null, "Nothing to apply: pass filePath to apply what a XAML file now holds, or newXaml with "
					+ "oldXaml to apply markup that is not on disk.");
			}

			if (!hasOld)
			{
				return (null, "Nothing to diff against: pass filePath rather than newXaml and this side keeps track "
					+ "of what it has already applied to the file, or pass oldXaml alongside newXaml.");
			}

			return (new ApplyInputs { OldXaml = oldXaml, NewXaml = newXaml! }, null);
		}

		if (hasNew)
		{
			return (null, "filePath and newXaml both say what the markup is now, so pass one: filePath to apply what "
				+ "the file holds, newXaml to apply markup that is not on disk.");
		}

		string full;
		try
		{
			full = Path.GetFullPath(filePath!);
		}
		catch (Exception exception)
		{
			return (null, $"'{filePath}' is not a usable path: {exception.Message}");
		}

		if (!File.Exists(full)) return (null, $"There is no file at {full}.");

		string current;
		try
		{
			current = File.ReadAllText(full);
		}
		catch (Exception exception)
		{
			return (null, $"Could not read {full}: {exception.Message}");
		}

		// Refused rather than recorded, and this is the reason the check is worth its lines. A first
		// apply records what it read as the baseline for the next one, so recording markup that does
		// not parse would leave every apply after it diffing against something unparseable -- a call
		// reporting a parse error about a file the caller has since fixed, with nothing it can do to
		// say so.
		if (!RoseMcp.XamlDiff.XamlDiff.Parses(current, out var reason))
		{
			return (null, $"{full} is not markup this can diff, so nothing was recorded or applied: {reason}");
		}

		// An explicit old version wins over the baseline and still refreshes it. The caller is telling
		// this side something it had no way to know, and the applies after it should carry on from
		// there rather than needing to be told again.
		if (hasOld) return (new ApplyInputs { OldXaml = oldXaml, NewXaml = current, SourcePath = full }, null);

		var plan = _baselines.Prepare(full, current, AgeOf(pid, full));

		return (new ApplyInputs { OldXaml = plan.OldXaml, NewXaml = current, SourcePath = full, Note = plan.Note }, null);
	}

	/// <summary>
	/// What can be said about a file's last write against the moment the target started running. It
	/// decides only the note on a first apply, and says "cannot tell" rather than assuming: "changed
	/// since the app started" is a claim about the file, and a process that will not give its start
	/// time is no evidence either way.
	/// </summary>
	private static XamlBaselineAge AgeOf(int pid, string path)
	{
		try
		{
			using var process = Process.GetProcessById(pid);

			return File.GetLastWriteTimeUtc(path) > process.StartTime.ToUniversalTime()
				? XamlBaselineAge.ChangedSinceTargetStarted
				: XamlBaselineAge.UnchangedSinceTargetStarted;
		}
		catch (Exception)
		{
			return XamlBaselineAge.Unknown;
		}
	}

	/// <summary>What an apply will diff, once the ways of asking for it have been reconciled.</summary>
	private sealed record ApplyInputs
	{
		/// <summary>Null when there is nothing to diff against; <see cref="Note"/> then says why.</summary>
		public string? OldXaml { get; init; }

		public required string NewXaml { get; init; }

		/// <summary>The file the markup came from, when it came from one. Keys the baseline.</summary>
		public string? SourcePath { get; init; }

		public string? Note { get; init; }
	}

	/// <summary>
	/// The provider's name for an edit kind. One place, because the command and the lookup of its
	/// result have to agree exactly -- they are keyed on this string, so two spellings of it would
	/// apply the edit and then report it as "not reported".
	/// </summary>
	private static string Op(XamlEditKind kind) => kind switch
	{
		XamlEditKind.SetProperty => "SetProperty",
		XamlEditKind.ClearProperty => "ClearProperty",
		XamlEditKind.RemoveChild => "RemoveChild",
		XamlEditKind.AddChild => "AddChild",
		_ => kind.ToString(),
	};

	/// <summary>
	/// One command line: op, target, property, value type, value, arg, index. The last two are only
	/// used by a structural command, and the shape is fixed so the provider can read positionally
	/// without every command having to carry every field.
	/// </summary>
	private static string Line(string op, string target, string property, string valueType, string value, string arg, int index) =>
		string.Join('\t', op, target, property, valueType, value, arg, index.ToString(CultureInfo.InvariantCulture));

	/// <summary>
	/// How a command's result is found again. The arg is part of it: without it, one slot given two
	/// children produces two rows keyed identically, and the second child's outcome silently replaces
	/// the first's.
	/// </summary>
	private static string Key(string op, string target, string property, string arg) =>
		string.Join('\t', op, target, property, arg);

	/// <summary>The command for one build step, with the key its result will come back under.</summary>
	private static (string Line, string Key) Command(XamlStep step) => step.Kind switch
	{
		XamlStepKind.Create => (
			Line("CreateInstance", step.Target, step.TypeName ?? string.Empty, string.Empty, string.Empty, string.Empty, 0),
			Key("CreateInstance", step.Target, step.TypeName ?? string.Empty, string.Empty)),

		XamlStepKind.SetProperty => (
			Line("SetProperty", step.Target, step.Property ?? string.Empty, step.ValueType ?? string.Empty, step.Value ?? string.Empty, string.Empty, 0),
			Key("SetProperty", step.Target, step.Property ?? string.Empty, string.Empty)),

		_ => (
			Line("AddChild", step.Target, string.Empty, string.Empty, string.Empty, step.Child ?? string.Empty, step.Index),
			Key("AddChild", step.Target, string.Empty, step.Child ?? string.Empty)),
	};

	/// <summary>
	/// What to report for one edit, given the commands it turned into.
	/// <para>
	/// An edit built from several commands is only applied if every one of them was. Reporting the last
	/// outcome, or the first, would let an addition whose element was created and then failed to attach
	/// come back as a success -- and the caller would go looking for an element that exists and is in
	/// nobody's tree.
	/// </para>
	/// </summary>
	private static string Outcome(List<string> keys, Dictionary<string, string> statuses)
	{
		if (keys.Count == 0) return "unsupported: this edit is not applied live yet";

		foreach (var key in keys)
		{
			var status = statuses.GetValueOrDefault(key, "not reported");
			if (status != "applied") return status;
		}

		return "applied";
	}

	private static Dictionary<string, string> ParseApplyResults(IEnumerable<string> lines)
	{
		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in lines)
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields.Length < 4) continue;

			// Keyed exactly the way the command was sent -- op, target, property, arg -- so each result can
			// be found again. The arg comes after the status and may be missing from an older provider's row.
			var arg = fields.Length > 4 ? Unescape(fields[4]) : string.Empty;
			statuses[Key(fields[0], Unescape(fields[1]), Unescape(fields[2]), arg)] = fields[3];
		}

		return statuses;
	}

	/// <summary>
	/// Makes sure the provider is loaded and answering on its pipe, injecting once if it is not. Returns
	/// null when it is ready, or the sentence saying why it is not.
	/// </summary>
	/// <remarks>
	/// Injection loads the provider and does nothing else. Every request is a message, because the work
	/// has to happen on the app's UI thread and injection was only ever the way onto that thread before
	/// there was a resident reader that could reach it through the dispatcher.
	/// <para>
	/// Once per session rather than once per call, which is the whole of what made a session accumulate
	/// advised taps -- each one receiving every mutation in the app, holding a copy of its tree, and
	/// costing the UI thread the next injection needs.
	/// </para>
	/// </remarks>
	private string? EnsureProvider(int pid)
	{
		if (_pipe?.Connected == true) return null;

		// Said, because one injection per session is the invariant and this is the only thing that can
		// break it. A pipe that drops sends the next call back through injection, which loads a second
		// tap into the app -- the condition that used to accumulate one per request. The previous tap
		// stands itself down, so the cost is bounded, but a session doing this repeatedly is a channel
		// failing quietly and it should not take a memory graph to notice.
		if (_injections > 0)
		{
			logger.LogWarning(
				"Injecting into pid {Pid} again (injection {Count}) because the XAML provider's pipe is not connected. "
					+ "One injection per session is the intent; more than one means the channel dropped.",
				pid,
				_injections + 1);
		}

		var (_, error) = Inject(pid);
		if (error is not null) return error;

		_injections++;

		if (_pipe?.Connected != true)
		{
			return "The XAML provider loaded but did not connect back on its pipe, which is how every request "
				+ $"reaches it. It was given {_bounds.Greeting.TotalSeconds:0.##}s to connect.";
		}

		return null;
	}

	/// <summary>
	/// What to tell a caller whose provider is connected and did not answer. Distinct from every other
	/// failure here: the provider is loaded and its pipe is up, so what has stopped is the app's UI
	/// thread, which is the one thing none of the other messages would send anyone to look at.
	/// </summary>
	private string Unanswered(string what) =>
		XamlChannelBounds.TimedOut($"the XAML provider, asked for {what}", _bounds.Snapshot);

	/// <summary>
	/// Stages the provider and injects it. Returns the working folder, or an error string when the
	/// provider is unavailable, staging fails, or injection is rejected.
	/// </summary>
	private (string? WorkDir, string? Error) Inject(int pid)
	{
		var (tap, tapError) = ResolveTap(pid);
		if (tap is null) return (null, tapError);

		var provider = ResolveProviderPath(tap);
		if (provider is null)
		{
			return (null, $"The XAML provider ({tap.ProviderFileName}) was not found for this host's architecture; build src/{tap.ProviderProjectName} for {ProviderPlatform()}.");
		}

		string workDir;
		string stagedProvider;
		try
		{
			(workDir, stagedProvider) = StageSandboxFolder(tap, provider);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Staging the XAML provider sandbox folder failed.");
			return (null, $"Could not stage the XAML provider: {exception.Message}");
		}

		// Retried, because the common failure here is transient and a one-shot message called it fatal.
		// The XAML diagnostics endpoint does not exist until the framework has built a tree, so a
		// session that has only just attached -- which is exactly when an agent asks -- gets
		// ERROR_NOT_FOUND for a second or two. A caller told "the target may have no XAML UI" about a
		// XAML app concludes the tool does not work on their app, and stops. It was reported that way
		// from a real session: the same call twelve seconds later returned 629 nodes.
		// Timed, because how long the endpoint took to answer is the one number that separates a session
		// that goes on working from one that wedges, and it was only ever recoverable by subtracting two
		// log timestamps by hand. InitializeXamlDiagnosticsEx does not return until the target's side has
		// created and sited the tap, so this measures the target's UI thread as much as our own work: a
		// handshake of seconds means that thread was saturated while we injected into it.
		var deadline = DateTime.UtcNow + _bounds.Endpoint;
		var handshake = Stopwatch.StartNew();
		var hr = 0;
		while (true)
		{
			// wszInitializationData is an arbitrary string handed to the TAP, and it already carries
			// the work directory, so the pipe name rides in the same slot -- no new plumbing to
			// establish the channel. Separated by '|', which cannot occur in a Windows path.
			var initData = _pipe is null ? workDir : $"{workDir}|{_pipe.Name}";

			var attempt = Initialise(tap, pid, stagedProvider, initData);
			if (attempt is null) return (null, WedgedInjectionDetail(pid));

			hr = attempt.Value;
			if (hr >= 0)
			{
				NoteProviderPipe();

				logger.LogInformation(
					"The target's XAML diagnostics endpoint answered in {HandshakeMs}ms on pid {Pid}.",
					handshake.ElapsedMilliseconds,
					pid);

				_endpointAnswered = true;
				return (workDir, null);
			}
			if (hr != ErrorNotFound || DateTime.UtcNow >= deadline) break;

			Thread.Sleep(250);
		}

		// Two failures, said apart. ERROR_NOT_FOUND after waiting is the endpoint never appearing,
		// which is what "no XAML UI" actually looks like; anything else is its own HRESULT and should
		// not be explained away as a missing UI.
		//
		// It no longer offers "or is not a packaged app". That was wrong twice over: packaging has
		// nothing to do with whether the endpoint appears, and unpackaged WinUI 3 is an ordinary
		// supported shape. The stack is known by the time this runs, so the message can name it
		// rather than guess at causes.
		// A third case, and it is the one that reads worst when it is folded into the second: the endpoint
		// answered earlier in this very session and has stopped. Neither "still starting" nor "no XAML UI"
		// can be true of an app that has already handed us a tree, so saying either sends the caller to
		// look at their own app. What it actually indicates is the target's UI thread no longer serving,
		// and the handshake time is quoted because a slow one is the warning that precedes this.
		var detail = hr != ErrorNotFound
			? $"InitializeXamlDiagnosticsEx failed (0x{hr:x8})."
			: _endpointAnswered
				? XamlChannelBounds.TimedOut("the target's XAML diagnostics endpoint", _bounds.Endpoint)
					+ $" It answered earlier in this session and has stopped (0x{ErrorNotFound:x8}), so the target is "
					+ "not starting up and does have a XAML UI. Its UI thread is no longer serving diagnostics: check "
					+ "whether the process is spinning a core, and if it is, the app will not recover and has to be "
					+ "restarted. Reads before this one took "
					+ $"{handshake.ElapsedMilliseconds}ms to be answered."
				: XamlChannelBounds.TimedOut("the target's XAML diagnostics endpoint", _bounds.Endpoint)
					+ $" It never appeared (0x{ErrorNotFound:x8}). The target was detected as {_stack!.Stack} because "
					+ $"{_stack.Reason}. A XAML app that is still starting can take a moment; if it persists, the "
					+ "target has no XAML UI.";

		logger.LogWarning(
			"The target's XAML diagnostics endpoint did not answer within {HandshakeMs}ms on pid {Pid} "
				+ "(0x{Hr:x8}); it had answered before in this session: {Answered}.",
			handshake.ElapsedMilliseconds,
			pid,
			hr,
			_endpointAnswered);

		return (null, detail);
	}

	/// <summary>
	/// One <c>InitializeXamlDiagnosticsEx</c> call, bounded. Returns the HRESULT, or null when the
	/// call did not come back inside <see cref="XamlChannelBounds.Injection"/>.
	/// <para>
	/// It is a blocking cross-process call that does not return until the target's side has created
	/// and sited the tap, and on WinUI 3 that means the app's UI thread has run the tap's body. A
	/// target whose UI thread is stuck below managed code therefore never returns from it -- which is
	/// how a suite run came to hang for fifty minutes on a first tree read, with the pipe logged as
	/// listening and no line after it.
	/// </para>
	/// <para>
	/// Bounded by running it on a thread of its own and abandoning that thread, because there is no
	/// other way to bound a blocking P/Invoke: the call cannot be cancelled and the native side holds
	/// no token. The thread is a background thread, so an abandoned injection cannot keep this process
	/// from exiting -- which matters more than reclaiming it, since the host has to be able to die
	/// with its client whatever the target is doing.
	/// </para>
	/// </summary>
	private int? Initialise(XamlTap tap, int pid, string stagedProvider, string initData)
	{
		var result = 0;
		var thread = new Thread(() => result = _initialise!(
			tap.EndpointName, (uint)pid, _diagnosticsPath, stagedProvider, tap.ProviderClsid, initData))
		{
			IsBackground = true,
			Name = "rose-xaml-inject",
		};

		thread.Start();
		if (thread.Join(_bounds.Injection)) return result;

		logger.LogWarning(
			"InitializeXamlDiagnosticsEx into pid {Pid} did not return within {Seconds}s; abandoning it.",
			pid,
			_bounds.Injection.TotalSeconds);

		return null;
	}

	/// <summary>
	/// What to tell a caller whose injection never came back. It names the channel, the bound, and the
	/// one thing that explains it, because from outside this is indistinguishable from every other
	/// way a tree read comes back empty.
	/// </summary>
	private string WedgedInjectionDetail(int pid) =>
		XamlChannelBounds.TimedOut("the XAML diagnostics injection call", _bounds.Injection)
			+ $" InitializeXamlDiagnosticsEx into pid {pid} did not return. It is served by the target's UI "
			+ "thread, so an app that is wedged, or stopped at a breakpoint, never lets it finish. The "
			+ "provider may still load if the app frees that thread.";

	/// <summary>
	/// Waits for the provider to connect back on the pipe, which every request rides. One that never
	/// connects is logged here and refused by <see cref="EnsureProvider"/>, so the bound it was given
	/// is the only thing that explains a session where nothing can reach the tap.
	/// </summary>
	private void NoteProviderPipe()
	{
		if (_pipe is null || _pipe.Connected) return;

		// The pipe says what it read as the greeting; what is worth adding is how long it was given,
		// because a provider loaded into a saturated UI thread and one that never loaded at all are
		// the same silence until the bound is in the line.
		if (_pipe.WaitForProvider(_bounds.Greeting) is null)
		{
			logger.LogWarning(
				"The XAML provider did not connect on {PipeName} within {Seconds}s; no request can reach it.",
				_pipe.Name,
				_bounds.Greeting.TotalSeconds);
		}
	}
	private (string WorkDir, string StagedProvider) StageSandboxFolder(XamlTap tap, string provider)
	{
		// Stage once per session and reuse: the first injection loads the provider DLL into the target,
		// which holds the file open, so a later injection cannot overwrite it -- and need not, since it
		// is the same provider. Each request re-injects from this one staged copy.
		if (_workDir is not null && _stagedProvider is not null && File.Exists(_stagedProvider))
		{
			return (_workDir, _stagedProvider);
		}

		var root = Path.Combine(Path.GetTempPath(), "RoseMcpXaml");

		// Before staging anything, clear out what earlier hosts left behind. Nothing ever deleted
		// these: 146 folders and 225.6 MB of them on the machine this was found on, each holding a
		// copy of the provider and each carrying a grant to ALL APPLICATION PACKAGES, so they are
		// world-readable directories accumulating in the user's TEMP.
		SweepDeadSandboxFolders(root);

		var workDir = Path.Combine(root, Environment.ProcessId.ToString());

		// Our own pid's folder goes too, because a pid is reusable. A host that draws a recycled pid
		// used to find a populated folder and, worse than a stale state file, load a stale *provider*:
		// the copy below was skipped whenever the DLL was already there, so deploying a new provider
		// and getting the old one was silent and every symptom pointed at the change just made. It is
		// also why the fast rebuild loop (build the provider, copy it over, restart the app) worked at
		// all -- a new pid meant a fresh copy -- and it would have stopped working the first time a
		// pid came round again.
		TryDeleteDirectory(workDir);
		Directory.CreateDirectory(workDir);

		// Unconditional. The overwrite was always there and always unreachable behind the existence
		// test; it can only be reached now because the folder above is cleared first, which is why
		// the two halves of this fix have to land together. One file copy per session is nothing
		// beside injecting into a process.
		var stagedProvider = Path.Combine(workDir, tap.ProviderFileName);
		File.Copy(provider, stagedProvider, overwrite: true);

		// ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2):
		// Modify grants read+execute to load the DLL and read commands, and write for the provider's
		// snapshot and log. Without this the sandboxed provider cannot touch the folder at all.
		//
		// Asked of the tap rather than done always (#74). An unpackaged WinUI 3 app is not in an
		// AppContainer and needs none of it, and granting anyway would leave a world-readable
		// directory in TEMP for every session, for nothing.
		if (tap.NeedsAppContainerGrants)
		{
			foreach (var sid in new[] { "*S-1-15-2-1", "*S-1-15-2-2" })
			{
				Icacls(workDir, $"/grant {sid}:(OI)(CI)(M)");
			}
		}

		// Created with the folder rather than lazily, because the name has to exist before the first
		// injection carries it.
		_pipe = new XamlProviderPipe(logger);
		_pipe.Listen();

		_workDir = workDir;
		_stagedProvider = stagedProvider;
		return (workDir, stagedProvider);
	}

	/// <summary>
	/// Deletes the sandbox folders belonging to hosts that are gone, the way <c>RoseMcp.Logging</c>
	/// prunes its own sessions at startup.
	/// <para>
	/// A folder is named after the pid that made it, so "is that pid still running" is the whole test.
	/// A pid that has been recycled by some unrelated process reads as alive and its folder is kept,
	/// which is the safe direction to be wrong in: the cost is one abandoned folder until the next
	/// sweep, where deleting a live host's folder would pull the provider out from under it.
	/// </para>
	/// </summary>
	private void SweepDeadSandboxFolders(string root)
	{
		try
		{
			if (!Directory.Exists(root)) return;

			foreach (var folder in Directory.EnumerateDirectories(root))
			{
				if (!int.TryParse(Path.GetFileName(folder), out var pid)) continue;
				if (pid == Environment.ProcessId) continue; // Ours; the caller deals with it deliberately.
				if (IsAlive(pid)) continue;

				TryDeleteDirectory(folder);
			}
		}
		catch (Exception exception)
		{
			// Tidying, never the job: a folder that cannot be enumerated or removed costs disk and
			// nothing else, and failing a XAML call over it would be the wrong trade entirely.
			logger.LogDebug(exception, "Sweeping stale XAML provider sandbox folders under {Root} failed.", root);
		}
	}

	private static bool IsAlive(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false; // No process with that id.
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (Exception)
		{
			// Whatever is still held belongs to an app that has the provider loaded, and that app
			// outlives the debug session on purpose -- detaching leaves it running. The next host to
			// start sweeps it once this pid is gone, which is why the sweep and this go together.
		}
	}

	/// <summary>
	/// Asks the resident provider to give back the two framework interfaces it holds, and says what
	/// it answered.
	/// <para>
	/// Over the pipe and acknowledged, rather than left to the provider noticing the pipe close. Both
	/// paths exist, because a host that is killed asks nothing -- but only the acknowledged one can be
	/// reported, and a release nobody can observe is one nobody can tell from the leak it replaces.
	/// </para>
	/// <para>
	/// Called on detach rather than only on disposal, and the difference is what makes it observable:
	/// disposal happens as the host shuts down, after its client has closed the stdin that carried
	/// its log, so the one line saying whether the release happened is written where nothing is left
	/// to read it. Idempotent, so both still calling it is fine -- the provider answers the second
	/// with "already released".
	/// </para>
	/// <para>
	/// A session with no pipe cannot ask, and there is nothing else to ask through: a request reaches
	/// the provider on the pipe and nowhere else, and injecting again to say "stop" would load a second
	/// tap to release the first one's interfaces. That case is said rather than fixed.
	/// </para>
	/// </summary>
	public void EndProviderSession()
	{
		lock (_requests)
		{
			if (_pipe?.Connected != true)
			{
				logger.LogDebug(
					"No provider pipe to detach on; anything the provider still holds goes when the app does.");

				return;
			}

			var answered = _pipe.Request("detach", _bounds.Greeting);
			if (answered is null)
			{
				logger.LogWarning("The XAML provider did not acknowledge the detach, so it may still hold its interfaces.");
				return;
			}

			logger.LogInformation("The XAML provider {Answer} its diagnostics interfaces on detach.", answered);
		}
	}

	/// <summary>
	/// Ends the session in the app and then deletes this session's sandbox folder. The folder is best
	/// effort by nature: the staged provider is loaded into an app that is meant to still be running
	/// afterwards, so the DLL is held open and only the next host's sweep can finish the job.
	/// </summary>
	public void Dispose()
	{
		// Under the same lock as a request, or the folder can be deleted from under one in flight.
		lock (_requests)
		{
			if (_workDir is null) return;

			// Asked before the pipe goes, because afterwards there is no way to ask and no way to hear
			// the answer. The provider holds an IXamlDiagnostics and an IVisualTreeService per
			// injection and released them from nowhere any caller reaches, so they were held for the
			// life of the app -- and the app outlives the session deliberately, which is what turns a
			// leak per session into a leak that accumulates.
			EndProviderSession();

			_pipe?.Dispose();
			_pipe = null;

			TryDeleteDirectory(_workDir);
			_workDir = null;
			_stagedProvider = null;
		}
	}

	private void Icacls(string path, string arguments)
	{
		try
		{
			var start = new ProcessStartInfo("icacls.exe", $"\"{path}\" {arguments}")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using var process = Process.Start(start);
			if (process is null) return;

			// Bounded like every other wait on this path. A grant that never finishes is a tool call
			// that never returns, and the AppContainer grants are the last thing between staging the
			// provider and injecting it -- so a wait with no bound here hangs exactly where the pipe
			// has just been logged as listening.
			if (process.WaitForExit((int)_bounds.Grant.TotalMilliseconds)) return;

			logger.LogWarning(
				"icacls {Arguments} on {Path} did not finish within {Seconds}s; the provider may not be able "
					+ "to reach the work folder.",
				arguments,
				path,
				_bounds.Grant.TotalSeconds);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "icacls {Arguments} on {Path} failed.", arguments, path);
		}
	}

	private static List<LiveXamlNode> ParseTree(IEnumerable<string> lines)
	{
		var nodes = new List<LiveXamlNode>();
		foreach (var line in lines)
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields.Length < 5) continue;
			if (!ulong.TryParse(fields[0], out var handle) || !ulong.TryParse(fields[1], out var parent) || !int.TryParse(fields[2], out var childIndex))
			{
				continue;
			}

			var name = Unescape(fields[4]);
			var declaredIn = fields.Length > 5 ? Unescape(fields[5]) : string.Empty;
			var declaredAt = fields.Length > 6 && int.TryParse(fields[6], out var parsedLine) ? parsedLine : 0;

			// A provider older than this host writes no ninth column. Read as "no address" rather than as
			// a bad row: the provider is staged from the install beside us, so the two ship together, but a
			// stale copy left behind in a sandbox would otherwise take the whole tree down with it.
			var address = fields.Length > 8 ? Unescape(fields[8]) : string.Empty;

			nodes.Add(new LiveXamlNode
			{
				Handle = handle,
				Parent = parent,
				ChildIndex = childIndex,
				TypeName = Unescape(fields[3]),
				Name = string.IsNullOrEmpty(name) ? null : name,
				File = string.IsNullOrEmpty(declaredIn) ? null : declaredIn,
				Line = declaredAt > 0 ? declaredAt : null,
				Address = string.IsNullOrEmpty(address) ? null : address,
			});
		}

		return nodes;
	}

	private static LiveXamlProperties ParseProperties(IEnumerable<string> lines, ulong handle)
	{
		string? typeName = null;
		string? elementFile = null;
		int? elementLine = null;
		int? elementColumn = null;
		var properties = new List<LiveXamlProperty>();

		foreach (var line in lines)
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields[0] == "E" && fields.Length >= 5)
			{
				typeName = EmptyToNull(Unescape(fields[1]));
				elementFile = EmptyToNull(Unescape(fields[2]));
				elementLine = ParsePositive(fields[3]);
				elementColumn = ParsePositive(fields[4]);
			}
			else if (fields[0] == "P" && fields.Length >= 10)
			{
				var isNull = fields[9] == "1";

				// Length-checked rather than assumed: an older provider staged in a recycled sandbox
				// folder writes ten columns, and the row is still worth reading without the eleventh.
				var unrenderable = fields.Length > 10 && fields[10] == "1";
				properties.Add(new LiveXamlProperty
				{
					Name = Unescape(fields[1]),
					Value = isNull ? null : Unescape(fields[2]),
					ValueUnavailable = unrenderable,
					ValueType = EmptyToNull(Unescape(fields[3])),
					DeclaringType = EmptyToNull(Unescape(fields[4])),
					Provenance = fields[5],
					SourceFile = EmptyToNull(Unescape(fields[6])),
					SourceLine = ParsePositive(fields[7]),
					SourceColumn = ParsePositive(fields[8]),
				});
			}
		}

		return new LiveXamlProperties
		{
			Handle = handle,
			TypeName = typeName,
			SourceFile = elementFile,
			SourceLine = elementLine,
			SourceColumn = elementColumn,
			Properties = properties,
		};
	}

	private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

	private static int? ParsePositive(string field) => int.TryParse(field, out var value) && value > 0 ? value : null;

	private static string Unescape(string field)
	{
		if (field.IndexOf('\\') < 0) return field;

		var builder = new StringBuilder(field.Length);
		for (var i = 0; i < field.Length; i++)
		{
			if (field[i] == '\\' && i + 1 < field.Length)
			{
				var next = field[++i];
				builder.Append(next switch
				{
					't' => '\t',
					'r' => '\r',
					'n' => '\n',
					'\\' => '\\',
					_ => next,
				});
			}
			else
			{
				builder.Append(field[i]);
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// Which tap serves this target, resolved from the framework DLLs the process has loaded, with
	/// the initialiser bound from the library that tap names (#74, #76).
	/// <para>
	/// Asking first is the whole point. This used to assume UWP in four places, so a WinUI 3 target
	/// spent twenty seconds waiting for an endpoint that was never going to appear and then blamed
	/// the app for not being packaged. The fact that settles it is in the module list, and reading it
	/// costs microseconds.
	/// </para>
	/// <para>
	/// The initialising library is looked up in the target too, rather than loaded by bare name.
	/// UWP's is a system DLL and either route works; WinUI 3's is in a versioned, per-architecture
	/// WindowsAppRuntime framework package on no search path this process has, and the target is the
	/// only thing that knows which copy it is actually running.
	/// </para>
	/// </summary>
	private (XamlTap? Tap, string? Error) ResolveTap(int pid)
	{
		if (_tap is not null && _initialise is not null) return (_tap, null);

		// Re-probed while it is Unknown, which is "could not be determined" rather than "no XAML" and so
		// is not an answer worth keeping. A launched app is asked the moment its session goes Ready, well
		// before it has loaded a XAML framework, and caching that one early look made it the answer for
		// every XAML call the session went on to serve: an app with 116 modules kept being described from
		// the 25 it had at startup. An attached app never showed it, because by then the app is up.
		if (_stack is null or { Stack: XamlStack.Unknown }) _stack = XamlStackProbe.Detect(pid);

		var tap = XamlTaps.For(_stack.Stack);
		if (tap is null) return (null, $"{XamlTaps.NoTapReason(_stack.Stack)} ({_stack.Reason}).");

		// The target's own copy where it has one, and the bare name otherwise -- which is right for a
		// system DLL and is the only thing left to try for anything else.
		var library = XamlStackProbe.ModulePath(pid, tap.InitializeLibrary) ?? tap.InitializeLibrary;

		// Passed through as the tap declares it, which for WinUI 3 is the bare module name rather than
		// a path. Resolving it to the target's full path looks more careful and is not what the
		// framework's own samples do.
		_diagnosticsPath = tap.DiagnosticsModule;

		try
		{
			var handle = NativeLibrary.Load(library);
			var export = NativeLibrary.GetExport(handle, nameof(InitializeXamlDiagnosticsEx));
			_initialise = Marshal.GetDelegateForFunctionPointer<InitializeXamlDiagnosticsEx>(export);
		}
		catch (Exception exception)
		{
			// Its own failure, and said as one. A framework DLL that will not load is not the same as
			// a target with no XAML in it, and reporting it as the latter is what sent the last
			// person looking at their app instead of at their install.
			return (null, $"Could not bind {nameof(InitializeXamlDiagnosticsEx)} in {library}: {exception.Message}");
		}

		_tap = tap;
		return (tap, null);
	}

	/// <summary>
	/// Finds the tap's provider DLL for this host's architecture. The deciding is in
	/// <see cref="XamlProviderPath"/>, where a test can reach it; what is here is the two facts only a
	/// running host knows -- where it is installed and which RID it is.
	/// </summary>
	private static string? ResolveProviderPath(XamlTap tap) => XamlProviderPath.Resolve(new XamlProviderLookup(
		Environment.GetEnvironmentVariable("ROSEMCP_XAML_PROVIDER"),
		AppContext.BaseDirectory,
		RuntimeInformation.RuntimeIdentifier,
		ProviderPlatform(),
		tap.ProviderFileName,
		tap.ProviderProjectName));

	/// <summary>The provider build platform matching this host's architecture (x64 or arm64).</summary>
	private static string ProviderPlatform() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		_ => "x64",
	};

	/// <summary>
	/// <c>InitializeXamlDiagnosticsEx</c> as a delegate rather than a <c>DllImport</c>, because the
	/// library exporting it is per-stack (#74): UWP's is Windows.UI.Xaml.dll, and WinUI 3's is the
	/// WindowsAppSDK's own Microsoft.UI.Xaml.dll, which is not even on the default search path. A
	/// DllImport attribute can name exactly one library, which is the hard-coding this removes.
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
	private delegate int InitializeXamlDiagnosticsEx(
		string endPointName,
		uint pid,
		string? wszDllXamlDiagnostics,
		string wszTapDllName,
		Guid tapClsid,
		string wszInitializationData);
}
