using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;

namespace RoseMcp.Inspector.Panes;

/// <summary>
/// The session's breakpoints and tracepoints: what is set, whether it has bound, how often it has
/// fired, and the forms for adding another.
/// </summary>
public sealed partial class BreakpointsPane : UserControl
{
	/// <summary>
	/// How often the lists are re-read. Slower than the session poll because the only thing that
	/// moves on its own is a hit count, and a binding that lands late lands within two seconds.
	/// </summary>
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

	private OperatorClient? _client;
	private Action<Exception>? _report;
	private PollLoop? _poll;
	private InspectedSession? _session;

	public BreakpointsPane()
	{
		InitializeComponent();
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
	}

	private async void OnAddBreakpoint(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;

		var location = BreakpointLocation.Text.Trim();
		if (location.Length == 0) return;

		try
		{
			AddBreakpoint.IsEnabled = false;

			var set = await client.SetBreakpointAsync(
				session.SessionId,
				new SetBreakpointRequest
				{
					Location = location,
					AutoContinueSeconds = Seconds(BreakpointHold.Text),
					Condition = Trimmed(BreakpointCondition.Text),
				},
				CancellationToken.None);

			// Cleared only on success, so a refusal leaves what was typed there to be corrected.
			BreakpointLocation.Text = string.Empty;
			BreakpointCondition.Text = string.Empty;

			// An unbound breakpoint is not a failure -- its module may not be loaded -- so the host's
			// own sentence goes in front of the reader rather than an error.
			if (!set.Bound && set.Detail is { } detail) _report?.Invoke(new OperatorException(OperatorFailure.Refused, detail));

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

		var location = TracepointLocation.Text.Trim();
		if (location.Length == 0) return;

		try
		{
			AddTracepoint.IsEnabled = false;

			var added = await client.AddTracepointAsync(
				session.SessionId,
				new AddTracepointRequest
				{
					Location = location,
					LogMessage = Trimmed(TracepointMessage.Text),
					LogEveryNthHit = Seconds(TracepointEvery.Text),
				},
				CancellationToken.None);

			TracepointLocation.Text = string.Empty;
			TracepointMessage.Text = string.Empty;

			if (!added.Bound && added.Detail is { } detail) _report?.Invoke(new OperatorException(OperatorFailure.Refused, detail));

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
	private static int? Seconds(string? text) => int.TryParse(text?.Trim(), out var value) && value > 0 ? value : null;

	private static string? Trimmed(string? text) =>
		string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
