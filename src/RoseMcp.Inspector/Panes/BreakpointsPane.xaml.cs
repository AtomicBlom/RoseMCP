using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector.Panes;

/// <summary>
/// Where a breakpoint goes, and what is already set.
/// <para>
/// Choosing where is a search and then a click in the method's own source, rather than a box to type
/// a location into. Nothing about a location is memorable -- the namespace, the type and the method
/// all have to be exact for it to bind -- and an agentic session has no IDE open beside it to read
/// them out of. Clicking in the code also reaches the places a name cannot: a line inside a lambda
/// compiles into a method of the compiler's own, and a breakpoint there has to name that method.
/// </para>
/// </summary>
public sealed partial class BreakpointsPane : UserControl
{
	/// <summary>
	/// How often the lists are re-read. Slower than the session poll because the only thing that
	/// moves on its own is a hit count, and a binding that lands late lands within two seconds.
	/// </summary>
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

	/// <summary>
	/// How long typing has to stop before a search runs. A search reads every loaded module's
	/// metadata, so one per keystroke would spend most of its time answering queries nobody finished
	/// typing -- and the answers would arrive out of order.
	/// </summary>
	private static readonly TimeSpan TypingSettles = TimeSpan.FromMilliseconds(300);

	/// <summary>How many matches to ask for. A drop-down nobody scrolls past is the whole point of ranking them.</summary>
	private const int Matches = 25;

	private OperatorClient? _client;
	private Action<Exception>? _report;
	private PollLoop? _poll;
	private InspectedSession? _session;

	/// <summary>Cancels the search a keystroke before this one started, so only the last one lands.</summary>
	private CancellationTokenSource? _searching;

	/// <summary>The method being read, which is what the source rows and the chosen position belong to.</summary>
	private LiveMethodSource? _method;

	/// <summary>
	/// The location a breakpoint would be given: a position inside the method once one is clicked,
	/// and the method itself until then.
	/// </summary>
	private string? _chosen;

	public BreakpointsPane()
	{
		InitializeComponent();
		SearchStatus.Text = InspectorText.FindAMethod;
		ShowState();
	}

	public void Attach(OperatorClient client, Action<Exception> report)
	{
		_client = client;
		_report = report;
		_poll = new PollLoop(ReadAsync, Interval, exception => _report?.Invoke(exception));
	}

	public void Bind(InspectedSession session)
	{
		_session = session;
		BreakpointRows.ItemsSource = session.Breakpoints;
		TracepointRows.ItemsSource = session.Tracepoints;
		ShowState();
	}

	/// <summary>A pane nobody can see stops asking. See the same note on the events pane.</summary>
	public void Showing(bool visible)
	{
		if (visible && _session is not null) _poll?.Start();
		else _poll?.Stop();
	}

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		if (_client is not { } client || _session is not { } session) return;

		session.Absorb(await client.BreakpointsAsync(session.SessionId, cancellationToken));
		session.Absorb(await client.TracepointsAsync(session.SessionId, cancellationToken));

		ShowState();
	}

	private void ShowState()
	{
		var breakpoints = _session?.Breakpoints.Count ?? 0;
		var tracepoints = _session?.Tracepoints.Count ?? 0;

		NoBreakpoints.Text = breakpoints == 0 ? InspectorText.NoBreakpoints : string.Empty;
		NoBreakpoints.Visibility = breakpoints == 0 ? Visibility.Visible : Visibility.Collapsed;
		NoTracepoints.Text = tracepoints == 0 ? InspectorText.NoTracepoints : string.Empty;
		NoTracepoints.Visibility = tracepoints == 0 ? Visibility.Visible : Visibility.Collapsed;

		// Headings rather than counts hidden when empty: the two lists are what the bottom half is,
		// and a section that disappears when it has nothing in it is one a reader stops looking for.
		BreakpointCount.Text = breakpoints == 0 ? "Breakpoints" : $"Breakpoints ({breakpoints})";
		TracepointCount.Text = tracepoints == 0 ? "Tracepoints" : $"Tracepoints ({tracepoints})";
	}

	/// <summary>
	/// Searches after typing settles. The query is echoed on the answer, so one that arrives after
	/// the box has moved on is dropped rather than replacing a newer list with an older one.
	/// </summary>
	private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
	{
		if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
		if (_client is not { } client || _session is not { } session) return;

		var query = sender.Text;

		var searching = new CancellationTokenSource();
		var previous = Interlocked.Exchange(ref _searching, searching);
		previous?.Cancel();
		previous?.Dispose();

		try
		{
			await Task.Delay(TypingSettles, searching.Token);

			var found = await client.MethodsAsync(session.SessionId, query, Matches, searching.Token);
			if (searching.Token.IsCancellationRequested) return;
			if (!string.Equals(found.Query, MethodSearch.Text.Trim(), StringComparison.Ordinal)) return;

			// Assigned rather than mutated in place. The drop-down opens off the assignment, so a
			// bound collection that is cleared and refilled leaves the box with matches it never
			// shows -- a search that says it found something and appears to have done nothing.
			MethodSearch.ItemsSource = found.Matches;

			// And opened by hand. The control opens its own list from inside TextChanged, which has
			// returned long before this: the search waits for typing to settle and then for the
			// answer. Without this the box holds matches it never shows, which is a search that
			// reports what it found and appears to have done nothing.
			MethodSearch.IsSuggestionListOpen = found.Matches.Count > 0;

			SearchStatus.Text = DescribeSearch(found);
		}
		catch (OperationCanceledException)
		{
			// Another keystroke arrived. The search it started is the one that answers.
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	/// <summary>What the list of matches does not say for itself.</summary>
	private static string DescribeSearch(LiveMethodMatches found)
	{
		if (found.Detail is { Length: > 0 } detail) return detail;
		if (found.Matches.Count == 0) return InspectorText.NoMethodsFound;

		var counted = found.Total > found.Matches.Count
			? $"{found.Matches.Count} of {found.Total} matches"
			: Format.Count(found.Total, "match", "matches");

		return $"{counted} across {Format.Count(found.ModulesSearched, "module")}.";
	}

	private async void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
	{
		if (args.SelectedItem is not LiveMethodMatch match) return;

		// The box shows what was picked rather than the location that binds it. A location is a
		// machine's spelling of the same thing and reads as line noise in a search box.
		sender.Text = match.DisplayName;

		await ShowMethodAsync(match.Location);
	}

	/// <summary>
	/// Reads a method's source and its positions, and offers the method itself until a line is
	/// clicked -- so a method whose source is not on this machine is still something to break in.
	/// </summary>
	private async Task ShowMethodAsync(string location)
	{
		if (_client is not { } client || _session is not { } session) return;

		try
		{
			MethodSearch.IsEnabled = false;

			var method = await client.MethodSourceAsync(session.SessionId, location, CancellationToken.None);
			_method = method;
			_chosen = method.Location;

			MethodTitle.Text = method.DisplayName;
			MethodFile.Text = method.File is { Length: > 0 } file ? file : method.Module;
			ToolTipService.SetToolTip(MethodFile, method.File ?? method.Module);

			var rows = SourceView.Build(method);
			SourceRows.ItemsSource = rows;
			SourceFrame.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

			var detail = rows.Count == 0
				? $"{method.Detail} {InspectorText.NoSourceToPick}".Trim()
				: method.Detail;

			MethodDetail.Text = detail ?? string.Empty;
			MethodDetail.Visibility = detail is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

			ChosenPosition.Text = InspectorText.ChosenMethod(method.DisplayName);
			MethodPanel.Visibility = Visibility.Visible;
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			MethodSearch.IsEnabled = true;
		}
	}

	/// <summary>A line clicked in the source, which decides the instruction a breakpoint stops at.</summary>
	private void OnPickPosition(object sender, RoutedEventArgs args)
	{
		if (sender is not FrameworkElement { Tag: string location }) return;
		if (SourceRows.ItemsSource is not IReadOnlyList<SourceLineRow> rows) return;
		if (rows.FirstOrDefault(row => row.Location == location) is not { } row) return;

		_chosen = location;

		// Named with the method the instructions are really in, which for a line inside a lambda is
		// not the method on screen. A reader has no other way to see that.
		ChosenPosition.Text = InspectorText.Chosen(
			row.Note.Length > 0 ? row.Note : _method?.DisplayName ?? string.Empty,
			row.Line,
			row.OffsetLabel);
	}

	private void OnClearMethod(object sender, RoutedEventArgs args) => ClearMethod();

	/// <summary>
	/// Puts the picker back to an empty search box.
	/// <para>
	/// Done after a successful add as well as on the button, because the card is at its tallest
	/// exactly when a breakpoint has just been made: leaving the method open pushes the list holding
	/// the new row below the fold, so the one thing somebody wants to see is the one thing they have
	/// to go looking for.
	/// </para>
	/// </summary>
	private void ClearMethod()
	{
		_method = null;
		_chosen = null;
		SourceRows.ItemsSource = null;
		MethodPanel.Visibility = Visibility.Collapsed;
		SourceFrame.Visibility = Visibility.Collapsed;
		MethodSearch.Text = string.Empty;
		MethodSearch.ItemsSource = null;
		SearchStatus.Text = InspectorText.FindAMethod;
	}

	private async void OnAddBreakpoint(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (_chosen is not { } location) return;

		try
		{
			AddBreakpoint.IsEnabled = false;

			var set = await client.SetBreakpointAsync(
				session.SessionId,
				new SetBreakpointRequest
				{
					Location = location,
					AutoContinueSeconds = Whole(BreakpointHold.Text),
					Condition = Trimmed(BreakpointCondition.Text),
				},
				CancellationToken.None);

			// An unbound breakpoint is not a failure -- its module may not be loaded -- so the host's
			// own sentence goes in front of the reader rather than an error.
			if (!set.Bound && set.Detail is { } detail) _report?.Invoke(new OperatorException(OperatorFailure.Refused, detail));

			ClearMethod();
			_poll?.Kick();
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			AddBreakpoint.IsEnabled = true;
		}
	}

	private async void OnRemoveBreakpoint(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (sender is not FrameworkElement { Tag: string id }) return;

		try
		{
			session.Absorb(await client.RemoveBreakpointAsync(session.SessionId, id, CancellationToken.None));
			ShowState();
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	private async void OnAddTracepoint(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (_chosen is not { } location) return;

		try
		{
			AddTracepoint.IsEnabled = false;

			var added = await client.AddTracepointAsync(
				session.SessionId,
				new AddTracepointRequest
				{
					Location = location,
					LogMessage = Trimmed(TracepointMessage.Text),
					LogEveryNthHit = Whole(TracepointEvery.Text),
				},
				CancellationToken.None);

			if (!added.Bound && added.Detail is { } detail) _report?.Invoke(new OperatorException(OperatorFailure.Refused, detail));

			ClearMethod();
			_poll?.Kick();
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			AddTracepoint.IsEnabled = true;
		}
	}

	private async void OnRemoveTracepoint(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (sender is not FrameworkElement { Tag: string id }) return;

		try
		{
			session.Absorb(await client.RemoveTracepointAsync(session.SessionId, id, CancellationToken.None));
			ShowState();
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
	}

	/// <summary>A whole number a box holds, or null for an empty or unreadable one.</summary>
	private static int? Whole(string? text) => int.TryParse(text?.Trim(), out var value) && value > 0 ? value : null;

	private static string? Trimmed(string? text) =>
		string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
