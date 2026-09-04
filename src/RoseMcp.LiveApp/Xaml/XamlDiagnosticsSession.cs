using System.Diagnostics;
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
internal sealed class XamlDiagnosticsSession(ILogger logger)
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

	/// <summary>
	/// Reads a snapshot of the target's live visual tree, injecting the provider first. Returns a tree
	/// with a <see cref="LiveXamlTree.Detail"/> and no nodes -- never throws -- when the provider is not
	/// available for this architecture, injection fails, or the target has no XAML UI.
	/// </summary>
	public LiveXamlTree ReadTree(int pid)
	{
		var (workDir, error) = Inject(pid, "tree");
		if (error is not null) return new LiveXamlTree { Detail = error };

		if (!WaitForFile(Path.Combine(workDir!, "tree.ready"), SnapshotTimeout))
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
		var request = includeDefaults ? $"properties {handle} all" : $"properties {handle}";
		var (workDir, error) = Inject(pid, request);
		if (error is not null) return new LiveXamlProperties { Handle = handle, Detail = error };

		if (!WaitForFile(Path.Combine(workDir!, "properties.ready"), SnapshotTimeout))
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
		// Tokens rather than flags in the name: the provider parses them, and a request that does not
		// mention a toggle leaves whatever the person set on the toolbar alone.
		var request = "select"
			+ (includeAllElements ? " all" : string.Empty)
			+ (justMyXaml ? " myxaml" : " nomyxaml");

		var (workDir, error) = Inject(pid, request);
		if (error is not null) return new LiveXamlSelection { Detail = error };

		var readyFile = Path.Combine(workDir!, "select.ready");
		if (!WaitForFile(readyFile, SnapshotTimeout))
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

		// JustMyXaml is reported from what was actually set, not left to the record's default. It defaults
		// to true, so arming with justMyXaml: false answered "true" and a caller comparing the arming
		// response against a later selection saw a contradiction it could not explain.
		return new LiveXamlSelection
		{
			Armed = true,
			JustMyXaml = justMyXaml,
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
		var (workDir, error) = Inject(pid, "deselect");
		if (error is not null) return new LiveXamlSelection { Detail = error };

		var readyFile = Path.Combine(workDir!, "deselect.ready");
		if (!WaitForFile(readyFile, SnapshotTimeout))
		{
			return new LiveXamlSelection
			{
				Detail = "The provider was injected but did not confirm the deselect (the app may have no diagnostics UI layer).",
			};
		}

		var (mode, justMyXaml) = ReadOverlayState();
		var had = ReadFirstLine(readyFile) == "cleared";

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
		var (workDir, error) = Inject(pid, $"selecthandle {handle}");
		if (error is not null) return new LiveXamlSelection { Detail = error };

		// The provider writes the selection files and nothing else, so waiting on selection.ready is
		// the confirmation -- and it is the same file a click produces, which is what keeps one read
		// path for both routes.
		if (!WaitForFile(Path.Combine(workDir!, "selection.ready"), SnapshotTimeout))
		{
			return new LiveXamlSelection
			{
				Detail = $"The provider was injected but did not select handle {handle}. It may name something that "
					+ "is not an element, or something no longer in the tree; rose_xaml_tree lists what is.",
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
	/// Reads the element that was picked, if any. Deliberately does not inject: the toolbar is resident
	/// and owns the selection, and the person may have picked without this side being involved at all --
	/// which is the case this exists for. That is also why the armed state is read from the provider's
	/// own state file rather than remembered here.
	/// </summary>
	public LiveXamlSelection ReadSelection()
	{
		if (_workDir is null)
		{
			return new LiveXamlSelection { Detail = "No XAML tool has run against this session yet, so the in-app toolbar is not installed." };
		}

		var (mode, justMyXaml) = ReadOverlayState();
		var armed = mode == "select";
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
	private (string Mode, bool JustMyXaml) ReadOverlayState()
	{
		try
		{
			var stateFile = Path.Combine(_workDir!, "overlay.state");
			if (!File.Exists(stateFile)) return ("idle", true);

			// "<mode> justMyXaml=<0|1>". Tokenised, not compared whole: the file gained the toggle and
			// a parser matching the entire line against "select" then read every armed overlay as idle,
			// which the select-mode test caught precisely because it asserts the provider's own report
			// rather than what this side last asked for.
			var tokens = File.ReadAllText(stateFile).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var mode = tokens.Length > 0 ? tokens[0] : "idle";
			var justMyXaml = !tokens.Contains("justMyXaml=0", StringComparer.Ordinal);

			return (mode, justMyXaml);
		}
		catch (IOException)
		{
			return ("idle", true);
		}
	}

	/// <summary>
	/// Hot-reloads the target by diffing two XAML versions and applying the edits to the live tree (#12).
	/// Property edits are applied through the provider whether or not the element has an <c>x:Name</c>;
	/// structural edits are reported as not-yet-applied rather than dropped silently. Returns each
	/// computed edit with its outcome, plus the diff engine's notes.
	/// </summary>
	public LiveXamlReloadResult ApplyReload(int pid, string oldXaml, string newXaml)
	{
		XamlDiffResult diff;
		try
		{
			diff = RoseMcp.XamlDiff.XamlDiff.Compute(oldXaml, newXaml);
		}
		catch (Exception exception)
		{
			return new LiveXamlReloadResult { Detail = $"Could not diff the XAML: {exception.Message}" };
		}

		// The target goes to the provider exactly as the diff wrote it, `#name` or path alike. It used
		// to be narrowed to a bare x:Name first and anything else refused here without being tried,
		// which ruled out every element the markup never named -- and that is most of what a click
		// lands on, since anything inside a control template is unnamed. Whether an address resolves is
		// a question about the live tree, so it is asked where the live tree is; this side has only the
		// string, and a string cannot tell an unnameable element from an absent one.
		var commands = new List<string>();
		var plans = new List<(XamlEdit Edit, string? Target)>();
		foreach (var edit in diff.Edits)
		{
			var applies = edit.Kind is XamlEditKind.SetProperty or XamlEditKind.ClearProperty;
			plans.Add((edit, applies ? edit.Target : null));

			if (applies)
			{
				var op = edit.Kind == XamlEditKind.SetProperty ? "SetProperty" : "ClearProperty";
				commands.Add(string.Join('\t', op, edit.Target, edit.Property ?? string.Empty, edit.ValueType ?? string.Empty, edit.Value ?? string.Empty));
			}
		}

		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		if (commands.Count > 0)
		{
			var (workDir, error) = Inject(pid, "apply", commands);
			if (error is not null) return new LiveXamlReloadResult { Detail = error };

			if (!WaitForFile(Path.Combine(workDir!, "apply.ready"), SnapshotTimeout))
			{
				return new LiveXamlReloadResult { Detail = "The XAML provider was injected but did not report the apply in time." };
			}

			statuses = ParseApplyResults(Path.Combine(workDir!, "apply.tsv"));
		}

		var results = new List<LiveXamlEditResult>();
		foreach (var (edit, target) in plans)
		{
			string status;
			if (target is null)
			{
				status = "unsupported: structural edits are not applied live yet";
			}
			else
			{
				var op = edit.Kind == XamlEditKind.SetProperty ? "SetProperty" : "ClearProperty";
				status = statuses.GetValueOrDefault($"{op}\t{target}\t{edit.Property}", "not reported");
			}

			results.Add(new LiveXamlEditResult
			{
				Kind = edit.Kind.ToString(),
				Target = edit.Target,
				Property = edit.Property,
				Value = edit.Value,
				Status = status,
			});
		}

		return new LiveXamlReloadResult
		{
			Applied = results.Count(result => result.Status == "applied"),
			Results = results,
			Notes = diff.Notes,
		};
	}

	private static Dictionary<string, string> ParseApplyResults(string applyFile)
	{
		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in File.ReadLines(applyFile, Encoding.UTF8))
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields.Length < 4) continue;

			// Keyed by op, target name, property -- the same shape the command was sent as.
			statuses[$"{fields[0]}\t{Unescape(fields[1])}\t{Unescape(fields[2])}"] = fields[3];
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

		foreach (var stale in new[] { "tree.tsv", "tree.ready", "properties.tsv", "properties.ready", "apply.tsv", "apply.ready", "commands.tsv" })
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
				"select.ready", "selection.tsv", "selection.ready", "deselect.ready", "selection.gone",
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

			File.WriteAllText(Path.Combine(workDir, "request.txt"), request);
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

		var workDir = Path.Combine(Path.GetTempPath(), "RoseMcpXaml", Environment.ProcessId.ToString());
		Directory.CreateDirectory(workDir);

		var stagedProvider = Path.Combine(workDir, ProviderFileName);
		if (!File.Exists(stagedProvider))
		{
			File.Copy(provider, stagedProvider, overwrite: true);
		}

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

	private static bool WaitForFile(string path, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			if (File.Exists(path)) return true;
			Thread.Sleep(100);
		}

		return File.Exists(path);
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (Exception)
		{
			// A stale file we cannot delete is handled by the ready-marker wait, not fatal here.
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
