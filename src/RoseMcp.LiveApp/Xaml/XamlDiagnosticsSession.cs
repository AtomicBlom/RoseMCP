using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.XamlDiff;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Injects the RoseMcp.Xaml.Uwp.Tap diagnostics provider into the target and reads back what it reports (#2/#3).
/// <c>InitializeXamlDiagnosticsEx</c> (exported from Windows.UI.Xaml.dll) loads the provider into the
/// app by pid; the two ends exchange tab-separated files through a working folder this side stages and
/// grants the app's AppContainer rights to, since the provider runs sandboxed and cannot read Program
/// Files or write arbitrary paths. The provider must match the target's architecture, which is this
/// host's architecture -- an x64 provider for a classic UWP app emulated on ARM64.
/// </summary>
internal sealed class XamlDiagnosticsSession(ILogger logger) : IDisposable
{
	// Must match CLSID_RoseTap in RoseMcp.Xaml.Uwp.Tap.cpp.
	private static readonly Guid ProviderClsid = new("7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6");

	// The well-known diagnostics endpoint; anything else makes InitializeXamlDiagnosticsEx return
	// ERROR_NOT_FOUND (0x80070490).
	private const string EndpointName = "VisualDiagConnection1";

	private const string ProviderFileName = "RoseMcp.Xaml.Uwp.Tap.dll";

	// HRESULT_FROM_WIN32(ERROR_NOT_FOUND): the well-known diagnostics endpoint is not there yet.
	private const int ErrorNotFound = unchecked((int)0x80070490);

	// Long enough for a XAML app to get its first tree up, short enough that a target which genuinely
	// has no XAML UI does not hold a tool call for an uncomfortable length of time.
	private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(15);

	private string? _workDir;
	private string? _stagedProvider;

	// The number stamped on the request being served, and echoed back by the provider on everything it
	// writes. Every handshake here used to be "does this file exist", with the host deleting the marker
	// before injecting -- so a delete that silently failed left the wait satisfied by the *previous*
	// request's marker, and the host went on to read an answer written before it asked the question.
	// The comment on TryDelete claimed the ready-marker wait handled an undeletable stale file; it
	// could not, because existence is the same either way. A number the host chose can tell them apart
	// (#57).
	private long _generation;

	// What this side has already sent to the app, per source file (#12). It is held here rather than by
	// the caller for two reasons: this is the only place that can tell whether an apply reached the
	// provider, and a caller that has just written a file no longer holds what was there before.
	private readonly XamlApplyBaseline _baselines = new();

	// One request at a time, and this is measured rather than defensive (#93). The host serves MCP
	// calls concurrently -- two tree reads issued together finish in the time of one, where serialised
	// they would take twice as long -- and everything below shares one work folder, one request.txt
	// and one generation counter, so two in flight collide on all three.
	//
	// What that looked like, on ten concurrent pairs against the probe: a request.txt that could not be
	// written because the other call held it, several fifteen-second waits for a snapshot the other
	// call's injection had already consumed, and once a tree of 22 elements where the app has 24,
	// returned with no detail set. That last one is the reason this is a lock and not a documented
	// limitation: a truncated tree reported as success feeds handles to every other tool.
	//
	// Serialising rather than giving each request its own folder, because the provider keeps its work
	// folder in a global and does everything on the app's UI thread. Two folders would need a different
	// provider, and would buy no parallelism from a single-threaded consumer. The wait can be long --
	// the endpoint timeout is twenty seconds -- and a slow correct answer is the trade being made.
	//
	// It has to be a re-entrant lock, and that is load-bearing: selecting by handle finishes by
	// calling ReadSelection, which takes this lock again on the same thread. System.Threading.Lock
	// counts recursion and lets that through, as Monitor did; a SemaphoreSlim does not and would
	// deadlock the call forever. Proven by Selects_a_xaml_element_by_handle_without_a_click, which
	// walks exactly that path -- a non-re-entrant lock hangs it rather than failing an assertion.
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
		var (workDir, error) = Inject(pid, "tree");
		if (error is not null) return new LiveXamlTree { Detail = error };

		if (!WaitForMarker(Path.Combine(workDir!, "tree.ready"), SnapshotTimeout))
		{
			return new LiveXamlTree { Detail = "The XAML provider was injected but did not produce a tree snapshot in time." };
		}

		try
		{
			var nodes = ParseTree(Path.Combine(workDir!, "tree.tsv"));
			logger.LogInformation("Read a XAML tree of {Count} element(s) from pid {Pid}.", nodes.Count, pid);
			return new LiveXamlTree { Nodes = nodes };
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Reading the XAML tree snapshot failed.");
			return new LiveXamlTree { Detail = $"Could not read the tree snapshot: {exception.Message}" };
		}
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
		var (workDir, error) = Inject(pid, request);
		if (error is not null) return new LiveXamlProperties { Handle = handle, Detail = error };

		if (!WaitForMarker(Path.Combine(workDir!, "properties.ready"), SnapshotTimeout))
		{
			return new LiveXamlProperties { Handle = handle, Detail = "The XAML provider was injected but did not produce the properties in time." };
		}

		try
		{
			var properties = ParseProperties(Path.Combine(workDir!, "properties.tsv"), handle);
			logger.LogInformation("Read {Count} propert(y/ies) for handle {Handle} from pid {Pid}.", properties.Count, handle, pid);
			return properties;
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Reading the XAML properties failed.");
			return new LiveXamlProperties { Handle = handle, Detail = $"Could not read the properties: {exception.Message}" };
		}
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
		var (workDir, error) = Inject(pid, "idle");
		if (error is not null) return new LiveXamlSelection { Detail = error };

		if (!WaitForMarker(Path.Combine(workDir!, "idle.ready"), SnapshotTimeout))
		{
			return new LiveXamlSelection { Detail = "The provider was injected but did not report select mode disarmed." };
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

		var (workDir, error) = Inject(pid, request);
		if (error is not null) return new LiveXamlSelection { Detail = error };

		var readyFile = Path.Combine(workDir!, "select.ready");
		if (!WaitForMarker(readyFile, SnapshotTimeout))
		{
			return new LiveXamlSelection { Detail = "The provider was injected but did not arm select mode (the app may have no diagnostics UI layer)." };
		}

		// The provider reports the extent XAML arranged its capture layer at, and a zero is checked
		// rather than assumed: an overlay that exists but was given no area is armed, invisible, and
		// cannot be clicked -- which is indistinguishable from working if all you check is that it
		// armed. That exact state shipped once, so it is now a reported failure.
		var (width, height) = ReadArmedExtent(readyFile);
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
		var (mode, recorded, known) = ReadOverlayState();
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
		var (workDir, error) = Inject(pid, "deselect");
		if (error is not null) return new LiveXamlSelection { Detail = error };

		var readyFile = Path.Combine(workDir!, "deselect.ready");
		if (!WaitForMarker(readyFile, SnapshotTimeout))
		{
			return new LiveXamlSelection
			{
				Detail = "The provider was injected but did not confirm the deselect (the app may have no diagnostics UI layer).",
			};
		}

		var (mode, justMyXaml, _) = ReadOverlayState();

		// The first token, not the whole line: the marker now carries the generation after its
		// verdict, and comparing the line entire would read every clear as "nothing was selected".
		var had = Verdict(readyFile) == "cleared";

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
		var (workDir, error) = Inject(pid, $"selecthandle {handle}");
		if (error is not null) return new LiveXamlSelection { Detail = error };

		// A marker of its own, and the difference is fifteen seconds (#89). This used to wait on
		// selection.ready, reasoning that the provider writes the selection files and nothing else --
		// true, and that is the problem: it writes them only when it has a selection to record. A
		// handle naming something that is not an element, or something since gone from the tree,
		// produced no file at all, so the refusal arrived as a snapshot timeout. One file cannot both
		// answer a request and survive one, which is the same lesson selection.ready taught from the
		// other side. A "no" now costs what a "yes" costs.
		var readyFile = Path.Combine(workDir!, "selecthandle.ready");
		if (!WaitForMarker(readyFile, SnapshotTimeout))
		{
			return new LiveXamlSelection
			{
				Detail = $"The provider was injected but did not answer about handle {handle}.",
			};
		}

		if (Verdict(readyFile) != "selected")
		{
			return new LiveXamlSelection
			{
				Detail = $"Handle {handle} was not selected. It may name something that is not an element, or "
					+ "something no longer in the tree; rose_xaml_tree lists what is.",
			};
		}

		return ReadSelection();
	}

	/// <summary>
	/// The first word of a provider request. The verb decides what a request means to this side; the
	/// tokens after it are the provider's business.
	/// </summary>
	private static string Verb(string request)
	{
		var space = request.IndexOf(' ', StringComparison.Ordinal);

		return space < 0 ? request : request[..space];
	}

	/// <summary>
	/// The first line of a provider ready file, trimmed, or empty when it could not be read. A file
	/// that has just appeared can still be mid-write, and an unreadable confirmation is better treated
	/// as no answer than as one.
	/// </summary>
	private static string ReadFirstLine(string path)
	{
		try
		{
			return File.ReadLines(path).FirstOrDefault()?.Trim() ?? string.Empty;
		}
		catch (IOException)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// The first token of a ready file: what the provider decided, without the generation it stamped
	/// after it. Read as a token rather than as the whole line, because the line grew a second field
	/// and an equality test against it would quietly answer "no" to every question.
	/// </summary>
	private static string Verdict(string path) =>
		ReadFirstLine(path).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

	/// <summary>
	/// Reads the element that was picked, if any. Deliberately does not inject: the toolbar is resident
	/// and owns the selection, and the person may have picked without this side being involved at all --
	/// which is the case this exists for. That is also why the armed state is read from the provider's
	/// own state file rather than remembered here.
	/// </summary>
	public LiveXamlSelection ReadSelection()
	{
		lock (_requests) return ReadSelectionCore();
	}

	private LiveXamlSelection ReadSelectionCore()
	{
		if (_workDir is null)
		{
			return new LiveXamlSelection { Detail = "No XAML tool has run against this session yet, so the in-app toolbar is not installed." };
		}

		// This call deliberately does not inject, so the state file it reads was written for whatever
		// request went last. That is the current state and can be trusted -- unless the provider never
		// stamped it with that request's generation, which means it is not this side's answer to read.
		var (mode, justMyXaml, known) = ReadOverlayState();
		var armed = known && mode == "select";
		var selectionFile = Path.Combine(_workDir, "selection.tsv");
		if (!File.Exists(selectionFile))
		{
			// A selection that went away on its own says why (#51). Without this the answer is
			// "nothing has been picked yet", which is true and useless: something *was* picked, the
			// app took it away, and the caller is left wondering whether their select ever worked.
			var gone = ReadFirstLine(Path.Combine(_workDir, "selection.gone"));

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

			foreach (var line in File.ReadLines(selectionFile, Encoding.UTF8))
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
	/// Parses the provider's "armed &lt;width&gt;x&lt;height&gt;" marker. An unreadable or unexpected
	/// marker reports zero, which the caller treats as a failure -- the safe direction, since the whole
	/// point of the number is to catch an overlay that cannot be used.
	/// </summary>
	private static (int Width, int Height) ReadArmedExtent(string readyFile)
	{
		try
		{
			var parts = File.ReadAllText(readyFile).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2) return (0, 0);

			var extent = parts[1].Split('x');
			if (extent.Length != 2) return (0, 0);

			return (int.TryParse(extent[0], out var width) ? width : 0, int.TryParse(extent[1], out var height) ? height : 0);
		}
		catch (IOException)
		{
			return (0, 0);
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
			var treeFile = Path.Combine(_workDir!, "tree.tsv");
			if (!File.Exists(treeFile)) return [];

			return ParseTree(treeFile).ToDictionary(node => node.Handle);
		}
		catch (Exception exception) when (exception is IOException or ArgumentException)
		{
			return [];
		}
	}

	/// <summary>
	/// What the in-app toolbar says its mode is. Absent or unreadable counts as idle: the file is only
	/// ever a hint about a UI the person controls, and no tool should fail because it is missing.
	/// </summary>
	private (string Mode, bool JustMyXaml, bool Known) ReadOverlayState()
	{
		try
		{
			var stateFile = Path.Combine(_workDir!, "overlay.state");
			if (!File.Exists(stateFile)) return ("idle", true, false);

			// "<mode> justMyXaml=<0|1> gen=<n>". Tokenised, not compared whole: the file gained the
			// toggle and a parser matching the entire line against "select" then read every armed
			// overlay as idle, which the select-mode test caught precisely because it asserts the
			// provider's own report rather than what this side last asked for.
			var line = File.ReadAllText(stateFile).Trim();
			var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var mode = tokens.Length > 0 ? tokens[0] : "idle";
			var justMyXaml = !tokens.Contains("justMyXaml=0", StringComparer.Ordinal);

			// Believed only when the provider wrote it for the request this side last sent. Every
			// value in this file is legal, so a line left by an earlier request is a wrong answer
			// with nothing to mark it as one -- and the defaults returned when the file is missing
			// are legal too, which made "not written yet" and "written as true" the same reading.
			// Whoever asks now gets to hear that it cannot be said (#57).
			var generation = GenerationIn(line);
			var known = generation is null || generation == _generation;

			return (mode, justMyXaml, known);
		}
		catch (IOException)
		{
			return ("idle", true, false);
		}
	}

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
			var (workDir, error) = Inject(pid, "apply", commands);
			if (error is not null) return new LiveXamlApplyResult { Detail = error };

			if (!WaitForMarker(Path.Combine(workDir!, "apply.ready"), SnapshotTimeout))
			{
				// The baseline is deliberately left where it was, and the message says what that costs.
				// The commands were injected, so they may well have run; this side just cannot say. So
				// the next apply resends them, which is the caller's retry -- and for anything this
				// batch was adding, a retry that lands twice is a second copy.
				return new LiveXamlApplyResult
				{
					Detail = "The XAML provider was injected but did not report the apply in time. The edits may or "
						+ "may not have reached the app, so applying the same change again could add a second copy "
						+ "of anything this one was adding.",
				};
			}

			statuses = ParseApplyResults(Path.Combine(workDir!, "apply.tsv"));
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

	private static Dictionary<string, string> ParseApplyResults(string applyFile)
	{
		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in File.ReadLines(applyFile, Encoding.UTF8))
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
	/// Stages the provider, leaves the request for it, clears any stale output, and injects. Returns the
	/// working folder, or an error string when the provider is unavailable, staging fails, or injection
	/// is rejected. Each request re-injects because the provider does its work on the app's UI thread at
	/// SetSite.
	/// </summary>
	private (string? WorkDir, string? Error) Inject(int pid, string request, IReadOnlyList<string>? commands = null)
	{
		var provider = ResolveProviderPath();
		if (provider is null)
		{
			return (null, $"The XAML provider ({ProviderFileName}) was not found for this host's architecture; build src/RoseMcp.Xaml.Uwp.Tap for {ProviderPlatform()}.");
		}

		string workDir;
		string stagedProvider;
		try
		{
			(workDir, stagedProvider) = StageSandboxFolder(provider);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Staging the XAML provider sandbox folder failed.");
			return (null, $"Could not stage the XAML provider: {exception.Message}");
		}

		foreach (var stale in new[] { "tree.tsv", "tree.ready", "properties.tsv", "properties.ready", "apply.tsv", "apply.ready", "commands.tsv", "idle.ready" })
		{
			TryDelete(Path.Combine(workDir, stale));
		}

		// A selection outlives an injection, because the toolbar holding it does: reading the tree must
		// not throw away an element the person picked minutes ago. Arming a fresh pick clears it, and
		// so does an explicit deselect.
		//
		// Matched on the verb rather than the whole line, which is what this got wrong. The request
		// arming select mode always carries at least one token -- "select myxaml" is the minimum
		// EnterSelectMode can build -- so an equality test against "select" never once fired. The
		// consequence was quiet in the worst way: arming reported success, and rose_xaml_selection
		// then answered with the *previous* pick until a new click happened to land.
		var clearsSelection = Verb(request) is "select" or "selecthandle" or "deselect";
		if (clearsSelection)
		{
			foreach (var stale in new[]
			{
				"select.ready", "selection.tsv", "selection.ready", "selecthandle.ready", "deselect.ready", "selection.gone",
			})
			{
				TryDelete(Path.Combine(workDir, stale));
			}
		}

		try
		{
			if (commands is not null)
			{
				// No explicit encoding: the default is UTF-8 without a BOM, which the provider's narrow
				// command reader expects; Encoding.UTF8 would prepend a BOM and corrupt the first op.
				File.WriteAllLines(Path.Combine(workDir, "commands.tsv"), commands);
			}

			// The generation goes on a second line, which the provider echoes onto what it writes
			// back. A second line rather than another token or another file: the provider reads the
			// request from the first line only, so nothing that parses a verb can be disturbed by
			// this -- and one of those parsers matches on the line's own suffix ("properties <handle>
			// all"), which an appended token would have broken silently.
			_generation++;
			File.WriteAllText(Path.Combine(workDir, "request.txt"), $"{request}\n{_generation}\n");
		}
		catch (Exception exception)
		{
			return (null, $"Could not write the provider request: {exception.Message}");
		}

		// Retried, because the common failure here is transient and the old message called it fatal.
		// The XAML diagnostics endpoint does not exist until the framework has built a tree, so a
		// session that has only just attached -- which is exactly when an agent asks -- gets
		// ERROR_NOT_FOUND for a second or two. A caller told "the target may have no XAML UI or not be
		// a packaged app" about a packaged XAML app concludes the tool does not work on their app, and
		// stops. It was reported that way from a real session: the same call twelve seconds later
		// returned 629 nodes.
		var deadline = DateTime.UtcNow + EndpointTimeout;
		var hr = 0;
		while (true)
		{
			hr = InitializeXamlDiagnosticsEx(EndpointName, (uint)pid, null, stagedProvider, ProviderClsid, workDir);
			if (hr >= 0) return (workDir, null);
			if (hr != ErrorNotFound || DateTime.UtcNow >= deadline) break;

			Thread.Sleep(250);
		}

		// Two failures, said apart. ERROR_NOT_FOUND after waiting is the endpoint never appearing,
		// which is what "no XAML UI" actually looks like; anything else is its own HRESULT and should
		// not be explained away as a missing UI.
		var detail = hr == ErrorNotFound
			? $"The target's XAML diagnostics endpoint did not appear within {EndpointTimeout.TotalSeconds:0}s "
				+ $"(0x{ErrorNotFound:x8}). A XAML app that is still starting can take a moment; if it "
				+ "persists, the target has no XAML UI or is not a packaged app."
			: $"InitializeXamlDiagnosticsEx failed (0x{hr:x8}).";

		return (null, detail);
	}

	private (string WorkDir, string StagedProvider) StageSandboxFolder(string provider)
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
		var stagedProvider = Path.Combine(workDir, ProviderFileName);
		File.Copy(provider, stagedProvider, overwrite: true);

		// ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2):
		// Modify grants read+execute to load the DLL and read commands, and write for the provider's
		// snapshot and log. Without this the sandboxed provider cannot touch the folder at all.
		foreach (var sid in new[] { "*S-1-15-2-1", "*S-1-15-2-2" })
		{
			Icacls(workDir, $"/grant {sid}:(OI)(CI)(M)");
		}

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
	/// Deletes this session's sandbox folder. Best effort by nature, for the reason above: the staged
	/// provider is loaded into an app that is meant to still be running afterwards, so the DLL is
	/// held open and only the next host's sweep can finish the job.
	/// </summary>
	public void Dispose()
	{
		// Under the same lock as a request, or the folder can be deleted from under one in flight.
		lock (_requests)
		{
			if (_workDir is null) return;

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
			process?.WaitForExit();
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "icacls {Arguments} on {Path} failed.", arguments, path);
		}
	}

	private static List<LiveXamlNode> ParseTree(string treeFile)
	{
		var nodes = new List<LiveXamlNode>();
		foreach (var line in File.ReadLines(treeFile, Encoding.UTF8))
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

	private static LiveXamlProperties ParseProperties(string propertiesFile, ulong handle)
	{
		string? typeName = null;
		string? elementFile = null;
		int? elementLine = null;
		int? elementColumn = null;
		var properties = new List<LiveXamlProperty>();

		foreach (var line in File.ReadLines(propertiesFile, Encoding.UTF8))
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
	/// Waits for a marker the provider wrote <em>for this request</em>.
	/// <para>
	/// Existence alone was the test, and it is not enough. The host deletes the marker before
	/// injecting, so a file that exists afterwards normally does mean the provider has answered --
	/// but <see cref="TryDelete"/> swallows its failures, and the moment one fails the wait is
	/// satisfied instantly by the previous request's marker. Nothing downstream can tell the
	/// difference, so arming reports success and the read that follows returns the arm before it,
	/// which is a legal value and a wrong answer.
	/// </para>
	/// <para>
	/// A marker carrying no generation at all is accepted: that is a provider older than this host,
	/// and refusing it would turn a version skew into a timeout blaming the app's diagnostics layer.
	/// A marker carrying a <em>different</em> generation is precisely the stale one this rejects.
	/// </para>
	/// </summary>
	private bool WaitForMarker(string path, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (true)
		{
			if (IsCurrent(path)) return true;
			if (DateTime.UtcNow >= deadline) return false;

			Thread.Sleep(100);
		}
	}

	private bool IsCurrent(string path)
	{
		if (!File.Exists(path)) return false;

		var generation = GenerationIn(ReadFirstLine(path));

		return generation is null || generation == _generation;
	}

	/// <summary>The <c>gen=</c> token the provider stamped on a line, or null when it carries none.</summary>
	private static long? GenerationIn(string line)
	{
		foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (token.StartsWith("gen=", StringComparison.Ordinal) && long.TryParse(token.AsSpan(4), out var generation))
			{
				return generation;
			}
		}

		return null;
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (Exception)
		{
			// Not fatal, and no longer load-bearing: what a failure here used to produce was a wait
			// satisfied by the stale file it had just failed to remove. The generation on the marker
			// is what catches that now, so this is best-effort tidying rather than the thing keeping
			// the handshake honest.
		}
	}

	/// <summary>
	/// Finds the provider DLL for this host's architecture: an explicit override, a published layout
	/// beside the host (<c>xaml-provider/&lt;rid&gt;</c>), or the repo build output. Null when none is
	/// present, so the caller can report it rather than fault.
	/// </summary>
	private static string? ResolveProviderPath()
	{
		var configured = Environment.GetEnvironmentVariable("ROSEMCP_XAML_PROVIDER");
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

		var rid = RuntimeInformation.RuntimeIdentifier;
		var alongside = Path.Combine(AppContext.BaseDirectory, "xaml-provider", rid, ProviderFileName);
		if (File.Exists(alongside)) return alongside;

		var repositoryRoot = FindRepositoryRoot();
		if (repositoryRoot is null) return null;

		var providerBin = Path.Combine(repositoryRoot, "src", "RoseMcp.Xaml.Uwp.Tap", "bin", ProviderPlatform());
		if (!Directory.Exists(providerBin)) return null;

		return Directory.EnumerateFiles(providerBin, ProviderFileName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	/// <summary>The provider build platform matching this host's architecture (x64 or arm64).</summary>
	private static string ProviderPlatform() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		_ => "x64",
	};

	private static string? FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName;
	}

	[DllImport("Windows.UI.Xaml.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
	private static extern int InitializeXamlDiagnosticsEx(
		string endPointName,
		uint pid,
		string? wszDllXamlDiagnostics,
		string wszTapDllName,
		Guid tapClsid,
		string wszInitializationData);
}
