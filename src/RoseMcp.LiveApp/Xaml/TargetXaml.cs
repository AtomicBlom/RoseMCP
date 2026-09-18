using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// What a live-app session can be asked about the target's XAML, and the diagnostics session that
/// answers it.
/// <para>
/// Apart from the host because the host's job is a target and this one's is a tree. The provider is
/// made on the first question rather than at attach, since a debugger attaches to anything and most
/// targets have no XAML in them; once made it is kept, because a second tap in the app receives
/// every mutation and holds a copy of the tree for nothing.
/// </para>
/// <para>
/// Every answer here needs three facts about the target that only the host has, so they arrive per
/// request as an <see cref="XamlAsk"/> rather than being read from state this would have to be kept
/// in step with.
/// </para>
/// </summary>
internal sealed class TargetXaml(ILogger logger) : IDisposable
{
	private readonly Lock _gate = new();
	private XamlDiagnosticsSession? _session;

	/// <summary>The diagnostics session if one has been made, without making one.</summary>
	internal XamlDiagnosticsSession? Current
	{
		get { lock (_gate) return _session; }
	}

	/// <summary>The diagnostics session, made on first use.</summary>
	internal XamlDiagnosticsSession Ensure()
	{
		lock (_gate) return _session ??= new XamlDiagnosticsSession(logger);
	}

	/// <summary>Drops the diagnostics session, handing back whatever it held so a caller can end it.</summary>
	internal XamlDiagnosticsSession? Take()
	{
		lock (_gate)
		{
			var taken = _session;
			_session = null;
			return taken;
		}
	}

	public void Dispose() => Take()?.Dispose();

	/// <summary>
	/// Why a XAML request cannot be served at this instant, or null when it can be.
	/// <para>
	/// A target this session is holding is the case worth naming, and it is the one thing a debugger can
	/// know that nothing else can. <c>InitializeXamlDiagnosticsEx</c> does not return until the target's
	/// own UI thread has created and sited the tap, so a stopped target cannot serve the request at all
	/// -- and the bounds guarantee the shape of the failure rather than merely risking it: the endpoint
	/// is given twenty seconds and a held target auto-continues after thirty, so every such request
	/// expires first, and then blames the app for having no XAML UI. Visual Studio does not meet this
	/// because it is the debugger as well as the diagnostics client. So are we; the signal was simply
	/// never asked for.
	/// </para>
	/// <para>
	/// A stop an operator is holding names the hold, because the advice differs: an ordinary stop
	/// frees itself on the safety timer and waiting works, while a held one does not and waiting is
	/// the wrong thing to do.
	/// </para>
	/// </summary>
	private static string? WhyUnservable(CorDebugSession? session)
	{
		if (session?.CurrentStop() is not { } stop) return null;

		var freed = stop.Resume == LiveStopResume.HeldByOperator
			? "An operator is holding this stop, so the auto-continue timer will not free it: release the hold "
				+ "or continue the target."
			: "Resume the target and ask again -- a held target also releases itself on the auto-continue timer.";

		return "The target is stopped, so its UI thread cannot serve a XAML request: the diagnostics "
			+ "endpoint is created by that thread and this session is holding it. " + freed;
	}

	/// <summary>
	/// A failed XAML detail with the target's own heartbeat added, which is what separates the two
	/// causes the channel's HRESULT cannot.
	/// <para>
	/// A handshake that fails means either a target that is executing and not serving diagnostics, or a
	/// target that is not executing at all, and nothing about the process tells them apart: CPU time
	/// stops climbing either way, the window stops repainting either way, and a frozen app's threads
	/// are stopped through its job object without any of them being marked suspended, so thread state
	/// cannot see it either. What a target that has stopped executing does do is stop producing debug
	/// events, so the age of the last one is the discriminator.
	/// </para>
	/// <para>
	/// Two causes reach the second state. Injection itself is one, and it is the one measured here: the
	/// call is served by the target's UI thread, and when it does not return, that thread never runs
	/// again -- an age that starts climbing from the moment of the first injection is that, exactly. A
	/// backgrounded UWP app whose package has no debug mode is the other, since PLM freezes it, and a
	/// detach lifts debug mode while deliberately leaving the app running.
	/// </para>
	/// </summary>
	private static string WithHeartbeat(string detail, DebugEventBuffer events, ILogger logger)
	{
		if (events.Newest() is not { } newest) return detail;

		var age = DateTime.UtcNow - newest.TimestampUtc;

		// Logged as well as returned. The detail reaches whoever made the call; the log is where anyone
		// reading a run afterwards is, and a wedge is diagnosed from the log long after the result is
		// gone -- which it was, from a suite run, once this number existed to read.
		logger.LogWarning(
			"A XAML request failed and the target's last debug event ({Kind}) was {AgeSeconds:0.0}s ago.",
			newest.Kind,
			age.TotalSeconds);

		// Seconds rather than a verdict. Which ages are suspicious depends on what the target does when
		// it is idle -- a probe on a timer is silent for milliseconds, a real app for minutes -- and a
		// threshold picked here would be a guess presented as a diagnosis.
		return detail
			+ $" The target's last debug event ({newest.Kind}) was {age.TotalSeconds:0.0}s ago. If that is not "
			+ "recent the target has stopped executing rather than stopped answering: either the injection "
			+ "call never returned, which leaves the UI thread that serves it stuck, or the app is a "
			+ "backgrounded UWP one that PLM has frozen because its package no longer has debug mode.";
	}

	/// <summary>
	/// Injects the XAML diagnostics provider into the target and returns a snapshot of its live visual
	/// tree. Optionally rooted at a named element (its subtree only) and paged, since a real app's tree is
	/// large. Returns a tree carrying only a detail (no nodes) when the target has no XAML UI or the
	/// provider is unavailable, rather than throwing.
	/// </summary>
	internal LiveXamlTree ReadTree(XamlAsk ask, string? rootName, int offset, int limit)
	{
		var targetProcessId = ask.ProcessId;
		var installLocation = ask.InstallLocation;
		var xaml = Ensure();

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlTree { Detail = "This session has no target process to inspect." };
		}

		if (WhyUnservable(ask.Session) is { } held) return new LiveXamlTree { Detail = held };

		var tree = xaml.ReadTree(pid);
		if (tree.Detail is not null) return tree with { Detail = WithHeartbeat(tree.Detail, ask.Events, logger) };

		IReadOnlyList<LiveXamlNode> matched = tree.Nodes;
		if (!string.IsNullOrWhiteSpace(rootName))
		{
			var root = tree.Nodes.FirstOrDefault(node => node.Name == rootName);
			if (root is null) return new LiveXamlTree { Detail = $"No element named '{rootName}' is in the tree." };
			matched = Subtree(tree.Nodes, root);
		}

		var page = matched.Skip(Math.Max(0, offset)).Take(limit > 0 ? limit : int.MaxValue).ToList();

		// Carried on every page, because this is the answer that looks right when it is not: the
		// nodes below name source files, and a stale registration makes those files the wrong ones.
		//
		// The channel is carried for the same reason, and it is easy to lose here: paging builds a new
		// result rather than narrowing the one it was given, so a field the read below filled and this
		// line does not mention is dropped silently and reads as `not reported`.
		return new LiveXamlTree
		{
			Nodes = page,
			Total = matched.Count,
			InstallLocation = installLocation,
			Channel = tree.Channel,
		};
	}

	/// <summary>An element and all its descendants, from the flat node list, by walking parent handles.</summary>
	private static List<LiveXamlNode> Subtree(IReadOnlyList<LiveXamlNode> all, LiveXamlNode root)
	{
		var childrenByParent = all.Where(node => node.Handle != root.Handle)
			.ToLookup(node => node.Parent);

		var subtree = new List<LiveXamlNode>();
		var pending = new Queue<LiveXamlNode>();
		pending.Enqueue(root);
		while (pending.Count > 0)
		{
			var node = pending.Dequeue();
			subtree.Add(node);
			foreach (var child in childrenByParent[node.Handle])
			{
				pending.Enqueue(child);
			}
		}

		return subtree;
	}

	/// <summary>
	/// Reads one element's XAML properties (by the handle a tree snapshot reported) with provenance and,
	/// when the app carries source info, source location. Set properties only by default; the framework
	/// defaults are included on request.
	/// </summary>
	internal LiveXamlProperties ReadProperties(XamlAsk ask, ulong handle, bool includeDefaults)
	{
		var targetProcessId = ask.ProcessId;
		var xaml = Ensure();

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlProperties { Handle = handle, Detail = "This session has no target process to inspect." };
		}

		if (WhyUnservable(ask.Session) is { } held) return new LiveXamlProperties { Handle = handle, Detail = held };

		var properties = xaml.ReadProperties(pid, handle, includeDefaults);
		return properties.Detail is null
			? properties
			: properties with { Detail = WithHeartbeat(properties.Detail, ask.Events, logger) };
	}

	/// <summary>
	/// Arms one of the overlay's pointer modes -- select, so the next click picks an element, or
	/// rulers, so the picked one is measured from -- or disarms whichever is on.
	/// <para>
	/// Both positions of one switch, because the toolbar has always had both and only arming was
	/// reachable from here. Arming lays a pointer-capturing layer over the app; picking by handle does
	/// not take it away, since that never goes through the click path that ends the mode, so an agent
	/// that armed and changed its mind had left the app modal with no way back.
	/// </para>
	/// </summary>
	internal LiveXamlSelection EnterSelectMode(
		XamlAsk ask,
		bool includeAllElements,
		bool justMyXaml,
		bool arm = true,
		string mode = "select")
	{
		var targetProcessId = ask.ProcessId;
		var xaml = Ensure();

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		if (WhyUnservable(ask.Session) is { } held) return new LiveXamlSelection { Detail = held };

		var selection = arm
			? xaml.EnterSelectMode(pid, includeAllElements, justMyXaml, mode)
			: xaml.ExitSelectMode(pid);

		return selection.Detail is null ? selection : selection with { Detail = WithHeartbeat(selection.Detail, ask.Events, logger) };
	}

	/// <summary>Reads the element the user picked by clicking it in the running app.</summary>
	internal LiveXamlSelection ReadSelection()
	{
		var xaml = Current;

		return xaml?.ReadSelection()
			?? new LiveXamlSelection { Detail = "Select mode has not been entered for this session." };
	}

	/// <summary>
	/// Clears the picked element and the mark drawn over the app, so nothing is selected.
	/// <para>
	/// Injects, unlike <see cref="ReadSelection"/>, because the mark lives in the app's own visual
	/// tree and only the provider can take it down. Clearing the files from this side alone would
	/// leave an outline over the app pointing at a selection that no longer exists.
	/// </para>
	/// </summary>
	internal LiveXamlSelection ClearSelection(XamlAsk ask)
	{
		var targetProcessId = ask.ProcessId;
		var xaml = Ensure();

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		if (WhyUnservable(ask.Session) is { } held) return new LiveXamlSelection { Detail = held };

		var cleared = xaml.ClearSelection(pid);
		return cleared.Detail is null ? cleared : cleared with { Detail = WithHeartbeat(cleared.Detail, ask.Events, logger) };
	}

	/// <summary>
	/// Selects the element a handle names, without a click. See
	/// <see cref="XamlDiagnosticsSession.SelectByHandle"/> for why that matters.
	/// </summary>
	internal LiveXamlSelection SelectElement(XamlAsk ask, ulong handle)
	{
		var targetProcessId = ask.ProcessId;
		var xaml = Ensure();

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlSelection { Detail = "This session has no target process to inspect." };
		}

		if (WhyUnservable(ask.Session) is { } held) return new LiveXamlSelection { Detail = held };

		var selected = xaml.SelectByHandle(pid, handle);
		return selected.Detail is null ? selected : selected with { Detail = WithHeartbeat(selected.Detail, ask.Events, logger) };
	}

	/// <summary>
	/// Live-edits the target by diffing two XAML versions and applying the edits to its visual tree.
	/// Returns each computed edit with its outcome. Naming a file rather than passing both versions is
	/// the continuous-apply path (#12): the XAML session remembers what it has already sent.
	/// </summary>
	internal LiveXamlApplyResult Apply(XamlAsk ask, string? oldXaml, string? newXaml, string? filePath)
	{
		var targetProcessId = ask.ProcessId;
		var xaml = Ensure();

		if (targetProcessId is not { } pid)
		{
			return new LiveXamlApplyResult { Detail = "This session has no target process to inspect." };
		}

		if (WhyUnservable(ask.Session) is { } held) return new LiveXamlApplyResult { Detail = held };

		var applied = xaml.ApplyEdits(pid, oldXaml, newXaml, filePath);
		return applied.Detail is null ? applied : applied with { Detail = WithHeartbeat(applied.Detail, ask.Events, logger) };
	}
}

/// <summary>
/// The facts about the target a XAML request needs, read by the host under its own lock so
/// one request sees one consistent answer.
/// </summary>
/// <param name="ProcessId">The target's process id, or null when there is no target.</param>
/// <param name="InstallLocation">Where a packaged target was installed from, for reporting.</param>
/// <param name="Session">The debug session, which is what knows whether the target is held.</param>
/// <param name="Events">The target's event stream, whose newest entry is its heartbeat.</param>
internal readonly record struct XamlAsk(
	int? ProcessId,
	string? InstallLocation,
	CorDebugSession? Session,
	DebugEventBuffer Events);
