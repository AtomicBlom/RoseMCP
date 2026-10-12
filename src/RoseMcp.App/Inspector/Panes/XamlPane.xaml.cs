using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Contracts;

using RoseMcp.Ui.Core;
using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

using Windows.ApplicationModel.DataTransfer;

namespace RoseMcp.App.Inspector.Panes;

/// <summary>
/// The running app's visual tree, what each element's properties are, and what set them -- and what
/// live edits changed this session, from what to what, and who changed it.
/// <para>
/// Browsing a tree through an agent costs tokens for a question a person answers in seconds, which
/// is the whole reason this pane exists. Clicking a node here highlights it in the app; pressing
/// Select Element in the app selects it here. The tree can be searched and narrowed to the app's own
/// markup or to what changed, and while the target is stopped it stays readable as it was last read.
/// </para>
/// <para>
/// The record of live edits is the broker's, so it is waited on rather than polled and asking it costs
/// the app nothing: it is watched while the pane is on screen, stopped target or not, and the watch
/// stops when the pane is hidden, its session ends or its window closes.
/// </para>
/// <para>
/// Every call goes through one gate. The provider serves one XAML request at a time on the app's
/// own UI thread, and two interleaved on one pipe answer each other's questions -- measured once as
/// a tree of 22 elements where the app had 24, which is a truncated tree handing out handles for a
/// tree that is not there.
/// </para>
/// </summary>
public sealed partial class XamlPane : UserControl
{
	/// <summary>
	/// How often the app is asked what is selected. Half a second, because what it is watching for
	/// is a person clicking in another window: slower than that and the tree lags behind the click
	/// visibly, faster and it is a request nobody needed.
	/// </summary>
	private static readonly TimeSpan SelectionInterval = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// How many layout passes a reveal waits for the tree to build the row's container. Ten frames is
	/// far more than opening a deep element takes and short enough that a row the tree has virtualised
	/// away gives up in half a second.
	/// </summary>
	private const int RealiseAttempts = 10;

	/// <summary>Roughly a frame, which is the unit a layout pass happens in.</summary>
	private static readonly TimeSpan FramePause = TimeSpan.FromMilliseconds(50);

	/// <summary>
	/// One XAML request at a time. The host serialises them anyway; queueing here as well is what
	/// keeps a poll from stacking up behind a tree read that is taking thirty seconds.
	/// </summary>
	private readonly SemaphoreSlim _gate = new(1, 1);

	private OperatorClient? _client;
	private Action<Exception>? _report;
	private Action<NoteSubject, FrameworkElement>? _ask;
	private Action<Func<NoteSubject?>>? _regard;
	private InspectedSession? _session;
	private PollLoop? _selectionPoll;
	private ViewWatch<LiveXamlChangeList>? _changeWatch;
	private CancellationTokenSource? _reading;

	private bool _visible;
	private bool _stopped;
	private bool _readOnce;

	/// <summary>Whether the tree on screen came from a stopped target, so it is read again once the target runs.</summary>
	private bool _readWhileStopped;

	/// <summary>Whether the change watch is already failing, so a broker that went is said once rather than every retry.</summary>
	private bool _changesFailed;

	/// <summary>
	/// When the tree on screen was read: now-ish for a fresh read, or the time a stopped target's last read was taken.
	/// What a stopped target's tree is labelled with.
	/// </summary>
	private DateTime? _treeReadUtc;

	/// <summary>
	/// Whether the selection on screen is one this window set rather than one somebody clicked. A
	/// programmatic selection raises the same event a click does, and pushing it back to the app
	/// would answer the app's own pick with a copy of it.
	/// </summary>
	private bool _syncing;

	/// <summary>
	/// Whether the selection poll is already failing. It runs twice a second, so reporting every
	/// failure would be an error bar nobody could read -- and never reporting one would hide a pane
	/// that has quietly stopped following the app.
	/// </summary>
	private bool _pollFailed;

	public XamlPane() => InitializeComponent();

	/// <param name="client">The app's client.</param>
	/// <param name="report">Puts a failure in front of the reader.</param>
	/// <param name="ask">Opens the window's composer about a subject, beside an element.</param>
	/// <param name="regard">Says which element was last selected, which the window's shortcut asks about.</param>
	public void Attach(
		OperatorClient client,
		Action<Exception> report,
		Action<NoteSubject, FrameworkElement> ask,
		Action<Func<NoteSubject?>> regard)
	{
		_client = client;
		_report = report;
		_ask = ask;
		_regard = regard;

		// Quiet on failure: the body reports its own, once per run of them.
		_selectionPoll = new PollLoop(PollSelectionAsync, SelectionInterval, static _ => { });

		_changeWatch = new ViewWatch<LiveXamlChangeList>(
			(after, waitSeconds, cancellationToken) => _session is { } session
				? client.XamlChangesAsync(session.SessionId, after, waitSeconds, cancellationToken)
				: Task.FromCanceled<LiveXamlChangeList>(new CancellationToken(canceled: true)),
			list => list.Version,
			OnChanges,
			OnChangesFailed);
	}

	public void Bind(InspectedSession session)
	{
		_session = session;
		_readOnce = false;
		_readWhileStopped = false;
		_treeReadUtc = null;

		Tree.ItemsSource = session.Xaml.Roots;
		PropertyRows.ItemsSource = session.Xaml.Properties;
		FindBox.Text = session.Xaml.FindText;

		// A new session's record is a different list: what the watch held was another app's.
		_changeWatch?.Kick();

		Show(session.Xaml);
	}

	/// <summary>
	/// A pane nobody can see asks the app nothing. The selection poll is the reason this matters:
	/// it is a request into somebody's application twice a second, and a view in the background has
	/// no one to show the answer to.
	/// </summary>
	public void Showing(bool visible)
	{
		_visible = visible;

		if (!visible)
		{
			_selectionPoll?.Stop();
			_changeWatch?.Stop();
			Cancel();
			return;
		}

		_selectionPoll?.Start();
		if (_session is not null) _changeWatch?.Start();

		if (!_readOnce) _ = RefreshAsync();
	}

	/// <summary>
	/// Takes each poll of the session. A stopped target cannot answer about its visual tree at all, so
	/// the buttons that would ask it go dead and the pane says why -- while the tree it last read stays
	/// readable, and searchable, beside the stack. Running again, the element on screen is read again,
	/// and a tree that came from before the stop is replaced by a fresh one.
	/// </summary>
	public void Observe(SessionRow row)
	{
		var resumed = _stopped && !row.IsStopped;
		_stopped = row.IsStopped;

		if (resumed && _readWhileStopped) _readOnce = false;

		Buttons(_reading is null);

		if (_session is { } session) Show(session.Xaml);

		if (_visible && !_readOnce) _ = RefreshAsync();

		if (resumed && _visible && _session?.Xaml.Selected is { } selected) _ = ChooseAsync(selected, push: false);
	}

	/// <summary>
	/// Which buttons can be pressed. Refreshing reads the tree, which a stopped target answers with the one it last
	/// read; everything else asks the app's UI thread, which a stopped target cannot lend. Not while a read is in
	/// flight either: every one of these waits on the same gate, so a click during a thirty-second tree read does
	/// nothing visible and then snaps back, which reads as a button that does not work.
	/// </summary>
	private void Buttons(bool idle)
	{
		var live = idle && !_stopped;

		RefreshButton.IsEnabled = idle;
		PickButton.IsEnabled = live;
		JustMyXamlButton.IsEnabled = live;
		DeselectButton.IsEnabled = live;
		IncludeDefaultsBox.IsEnabled = live;
		MarkInAppButton.IsEnabled = live;
	}

	/// <summary>
	/// Re-reads the tree because an agent's live edit landed. An edit adds and removes elements, so
	/// a tree left alone is one that describes an app that has moved on -- and the rows are merged,
	/// so the reader keeps their place through it.
	/// </summary>
	public void AppliedXaml()
	{
		if (!_visible || _stopped) return;

		_ = RefreshAsync();
	}

	/// <summary>
	/// Reads the whole visual tree, merging it into the rows already on screen. A stopped target is answered with the
	/// tree it last gave, stamped with when, which asks the app nothing; the next read once it runs replaces it.
	/// </summary>
	private async Task RefreshAsync()
	{
		if (_client is not { } client || _session is not { } session) return;

		using var cancel = new CancellationTokenSource();

		await _gate.WaitAsync(CancellationToken.None);

		try
		{
			_reading = cancel;
			BusyText.Text = InspectorText.ReadingTree;
			Busy.Visibility = Visibility.Visible;
			Buttons(false);

			var tree = await client.XamlTreeAsync(session.SessionId, cancel.Token);

			// A stopped target with no tree to give answers with only a detail, which must not empty a tree already on
			// screen: that one is as good as anything the app could say now.
			var nothingToShow = tree.Nodes.Count == 0 && tree.AsOfUtc is null && session.Xaml.Count > 0 && _stopped;
			if (!nothingToShow)
			{
				session.Xaml.Absorb(tree);
				_treeReadUtc = tree.AsOfUtc ?? DateTime.UtcNow;
			}

			// Read once either way, so a stopped target is not asked every poll; one that came from before the stop is
			// read again once the target runs.
			_readOnce = true;
			_readWhileStopped = tree.AsOfUtc is not null || nothingToShow;
			Show(session.Xaml);
		}
		catch (OperationCanceledException)
		{
			// Cancelling is what the button is for. The tree already on screen stays.
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			_reading = null;
			Busy.Visibility = Visibility.Collapsed;
			Buttons(idle: true);
			_gate.Release();
		}
	}

	/// <summary>
	/// Takes the record of live edits: marks the changed rows, narrows the tree again where it shows only what changed,
	/// and puts each change beside its property. An answer about another session -- one bound before this, still in
	/// flight -- is dropped.
	/// </summary>
	private void OnChanges(LiveXamlChangeList list)
	{
		if (_session is not { } session || list.SessionId != session.SessionId) return;

		if (_changesFailed)
		{
			_changesFailed = false;
			if (DetailBar.Severity == InfoBarSeverity.Warning) DetailBar.IsOpen = false;
		}

		session.Xaml.Absorb(list);

		var dropped = XamlChangeText.Dropped(list.Dropped);
		ToolTipService.SetToolTip(
			ChangedButton,
			"Show only the elements a live edit changed in this session." + (dropped.Length > 0 ? $" {dropped}" : string.Empty));

		Show(session.Xaml);
	}

	/// <summary>
	/// A read of the record that failed, said once per run of them. A broker that is not answering is the session
	/// poll's to say, in the window's own words, so it is not said twice.
	/// </summary>
	private void OnChangesFailed(Exception exception)
	{
		if (_changesFailed || OperatorException.IsBrokerGone(exception)) return;

		_changesFailed = true;
		ShowDetail($"Not following the record of live edits: {exception.Message}", InfoBarSeverity.Warning);
	}

	/// <summary>
	/// Asks the app what is selected, which is how a click in the app reaches this window.
	/// <para>
	/// Read rather than remembered, because the person can pick and cancel from the app's own
	/// toolbar without this side being told -- so the app is the truth and the toggles follow it.
	/// </para>
	/// </summary>
	private async Task PollSelectionAsync(CancellationToken cancellationToken)
	{
		if (_client is not { } client || _session is not { } session) return;
		if (!_visible || _stopped) return;

		// Skipped rather than queued. A tree read can take thirty seconds, and a poll that waited
		// would spend it queueing sixty selection reads to run the moment it finished.
		if (!await _gate.WaitAsync(0, cancellationToken)) return;

		ulong picked = 0;

		try
		{
			var selection = await client.XamlSelectionAsync(session.SessionId, cancellationToken);

			session.Xaml.Absorb(selection);
			ShowToggles(session.Xaml);

			if (session.Xaml.PickedInApp(selection)) picked = selection.Handle;

			if (_pollFailed)
			{
				_pollFailed = false;
				DetailBar.IsOpen = false;
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception exception)
		{
			// Once per run of failures. Twice a second would be an error bar nobody could read, and
			// never would hide a pane that has quietly stopped following the app.
			if (!_pollFailed)
			{
				_pollFailed = true;
				ShowDetail($"Not following the app's selection: {exception.Message}", InfoBarSeverity.Warning);
			}

			return;
		}
		finally
		{
			_gate.Release();
		}

		// Outside the gate, because revealing can decide the tree is older than the app and re-read
		// it -- which takes the gate again.
		if (picked != 0) await RevealAsync(picked);
	}

	/// <summary>
	/// Brings a pick in the app onto the screen: the tree is opened down to it, it is selected, and
	/// it is scrolled to. A handle this tree does not have means the tree is older than the app, so
	/// it is re-read once before giving up.
	/// </summary>
	private async Task RevealAsync(ulong handle)
	{
		if (_session is not { } session) return;

		if (session.Xaml.Row(handle) is null)
		{
			await RefreshAsync();
			if (session.Xaml.Row(handle) is null) return;
		}

		if (session.Xaml.Row(handle) is not { } row) return;

		foreach (var ancestor in XamlTreeBuilder.Ancestors(session.Xaml, handle))
		{
			if (!ReferenceEquals(ancestor, row)) ancestor.IsExpanded = true;
		}

		// The properties first, because they are what the pick was for; the tree's own selection
		// catches up once it has laid out the levels just expanded.
		await ChooseAsync(row, push: false);
		await RevealRowAsync(row);
	}

	/// <summary>
	/// Makes an element the one being described: its properties are read, and the app is told to
	/// highlight it unless the app is where the choice came from.
	/// </summary>
	private async Task ChooseAsync(XamlNodeRow row, bool push)
	{
		if (_client is not { } client || _session is not { } session) return;

		Choose(row);
		_regard?.Invoke(() => SubjectOf(row));

		// A stopped target is asked nothing: its handles are not sent anywhere until it runs, and what changed on the
		// element is the broker's record, which needs no app to answer.
		if (_stopped)
		{
			session.Xaml.ShowChangesOnly(InspectorText.PropertiesNeedARunningTarget);
			Show(session.Xaml);
			return;
		}

		Show(session.Xaml);

		await _gate.WaitAsync(CancellationToken.None);

		try
		{
			if (push)
			{
				// The handle rather than the address: the tree has just handed us one, it is exact,
				// and an address is null for an element the tree could not compute one for.
				session.Xaml.LastPushedHandle = row.Handle;
				await client.SelectXamlAsync(session.SessionId, row.Handle.ToString(), CancellationToken.None);
			}

			var properties = await client.XamlPropertiesAsync(
				session.SessionId, row.Handle, session.Xaml.IncludeDefaults, CancellationToken.None);

			session.Xaml.Absorb(properties);
			Show(session.Xaml);
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Puts the selection where the model says, in both places that hold one: this window's, and the
	/// tree control's own.
	/// <para>
	/// Under the flag, because moving the control's selection raises the same event a click does, and
	/// answering the app's own pick with a copy of it is how the two ends chase each other. The row
	/// itself deliberately carries no selection flag: a third copy of one fact is how a row ends up
	/// highlighted while the pane beside it says nothing is selected.
	/// </para>
	/// <para>
	/// Through the node rather than the item. In ItemsSource mode the control deals in the
	/// <c>TreeViewNode</c> wrapping each item at both ends -- it hands one back on a click, and it
	/// ignores an item handed to it -- so selecting by item is a reveal that reads the right element
	/// and highlights no row at all.
	/// </para>
	/// </summary>
	private void Choose(XamlNodeRow? row)
	{
		if (_session is not { } session) return;

		_syncing = true;

		try
		{
			session.Xaml.Select(row);

			if (row is null)
			{
				Tree.SelectedNode = null;
				Tree.SelectedItem = null;
				return;
			}

			// Only a realised container has a node, which is why a reveal lays the tree out first.
			if (Tree.ContainerFromItem(row) is TreeViewItem container && Tree.NodeFromContainer(container) is { } node)
			{
				Tree.SelectedNode = node;
				return;
			}

			Tree.SelectedItem = row;
		}
		finally
		{
			_syncing = false;
		}
	}

	private void Show(XamlInspection xaml)
	{
		TreeCaption.Text = xaml.Count == 0
			? "Visual tree"
			: $"Visual tree ({Format.Count(xaml.Count, "element")})";

		ChangedButton.Content = xaml.ChangedCount == 0 ? "Changed" : $"Changed ({xaml.ChangedCount})";
		FilterNote.Text = xaml.FilterNote;
		FilterNote.Visibility = xaml.FilterNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		ShowToggles(xaml);
		ShowElement(xaml);

		// The stopped sentence wins, because it is the reason everything else on this pane is doing
		// nothing and every other detail here is about a read that is not going to happen. With a tree
		// on screen it says which moment that tree is from, since it is still worth reading.
		if (_stopped)
		{
			var readAt = xaml.AsOfUtc ?? _treeReadUtc;
			var said = xaml.Count > 0 && readAt is { } at
				? InspectorText.TreeAsOf(at)
				: InspectorText.XamlNeedsARunningTarget;

			ShowDetail(said, InfoBarSeverity.Informational);
			return;
		}

		if (_pollFailed || _changesFailed) return;

		if (xaml.HasTreeDetail) ShowDetail(xaml.TreeDetail, InfoBarSeverity.Informational);
		else DetailBar.IsOpen = false;
	}

	private void ShowToggles(XamlInspection xaml)
	{
		PickButton.IsChecked = xaml.Armed;
		JustMyXamlButton.IsChecked = xaml.JustMyXaml;
		IncludeDefaultsBox.IsChecked = xaml.IncludeDefaults;
		MarkInAppButton.IsChecked = xaml.ChangeMarks;
		ChangedButton.IsChecked = xaml.ChangedOnly;
	}

	private void ShowElement(XamlInspection xaml)
	{
		PropertiesDetail.Text = xaml.PropertiesDetail;
		PropertiesDetail.Visibility = xaml.HasPropertiesDetail ? Visibility.Visible : Visibility.Collapsed;

		AskButton.IsEnabled = xaml.Selected is not null;

		if (xaml.Selected is not { } selected)
		{
			ElementCaption.Text = "No element selected";
			NothingSelected.Text = InspectorText.NoElementSelected;
			NothingSelected.Visibility = Visibility.Visible;
			AddressLine.Visibility = Visibility.Collapsed;
			ObservedNote.Visibility = Visibility.Collapsed;
			return;
		}

		// The qualified name here, where there is room for it, rather than the tree's short one: two
		// elements in a tree can read as the same Border and be types from different assemblies.
		ElementCaption.Text = XamlNodeRow.Describe(selected.TypeName, selected.Name);
		NothingSelected.Visibility = Visibility.Collapsed;

		AddressText.Text = selected.Address ?? "(no address)";
		ToolTipService.SetToolTip(AddressText, selected.Address);
		CopyAddressButton.IsEnabled = selected.Address is { Length: > 0 };
		ElementWhere.Text = selected.Where;
		AddressLine.Visibility = Visibility.Visible;

		ObservedNote.Text = xaml.ObservedNote;
		ObservedNote.Visibility = xaml.ObservedNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void ShowDetail(string message, InfoBarSeverity severity)
	{
		DetailBar.Message = message;
		DetailBar.Severity = severity;
		DetailBar.IsOpen = message.Length > 0;
	}

	/// <summary>
	/// Selects a revealed row in the tree and scrolls to it, once the control has built a container
	/// for it.
	/// <para>
	/// Opening five levels takes several layout passes, and the control only has a node -- the thing it
	/// accepts as a selection -- once the container exists. So this tries over a handful of frames
	/// rather than once: how many passes it takes depends on how deep the element is, and selecting
	/// too early fails silently, leaving the right properties beside a tree that highlights nothing.
	/// </para>
	/// <para>
	/// Bounded, and best effort. A row the tree has virtualised away cannot be selected at all, and a
	/// reveal that never finishes would be worse than one that quietly did not scroll.
	/// </para>
	/// </summary>
	private async Task RevealRowAsync(XamlNodeRow row)
	{
		for (var attempt = 0; attempt < RealiseAttempts; attempt++)
		{
			Tree.UpdateLayout();

			if (Tree.ContainerFromItem(row) is TreeViewItem container && Tree.NodeFromContainer(container) is { } node)
			{
				_syncing = true;

				try
				{
					Tree.SelectedNode = node;
				}
				finally
				{
					_syncing = false;
				}

				container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.4 });
				return;
			}

			await Task.Delay(FramePause);
		}
	}

	private void Cancel() => _reading?.Cancel();

	private async void OnRefresh(object sender, RoutedEventArgs args) => await RefreshAsync();

	private void OnCancel(object sender, RoutedEventArgs args) => Cancel();

	private async void OnElementChosen(TreeView sender, TreeViewSelectionChangedEventArgs args)
	{
		if (_syncing || _session is null) return;

		var chosen = args.AddedItems.Count > 0 ? args.AddedItems[0] : Tree.SelectedItem;

		if (RowOf(chosen) is not { } row) return;
		if (ReferenceEquals(row, _session.Xaml.Selected)) return;

		await ChooseAsync(row, push: true);
	}

	/// <summary>
	/// The row behind whatever the tree hands back.
	/// <para>
	/// In ItemsSource mode the control is documented to surface the data item, and it can surface the
	/// <c>TreeViewNode</c> wrapping it instead. Taking only one of those is a click that reaches
	/// nothing, and it fails invisibly: a cast that did not match reads exactly like a click on empty
	/// space, so the row highlights and the pane beside it goes on saying nothing is selected.
	/// </para>
	/// </summary>
	private static XamlNodeRow? RowOf(object? chosen) => chosen switch
	{
		XamlNodeRow row => row,
		TreeViewNode { Content: XamlNodeRow row } => row,
		_ => null,
	};

	private async void OnTogglePick(object sender, RoutedEventArgs args) =>
		await SelectModeAsync(PickButton.IsChecked == true, _session?.Xaml.JustMyXaml ?? true);

	/// <summary>
	/// Changes what a pick prefers. Sent as a select-mode request with the arming left where it is,
	/// because the preference belongs to select mode rather than being a setting of this window.
	/// </summary>
	private async void OnToggleJustMyXaml(object sender, RoutedEventArgs args) =>
		await SelectModeAsync(_session?.Xaml.Armed ?? false, JustMyXamlButton.IsChecked == true);

	private async Task SelectModeAsync(bool arm, bool justMyXaml)
	{
		if (_client is not { } client || _session is not { } session) return;

		await _gate.WaitAsync(CancellationToken.None);

		try
		{
			var selection = await client.XamlSelectModeAsync(
				session.SessionId,
				new XamlSelectModeRequest { Arm = arm, JustMyXaml = justMyXaml },
				CancellationToken.None);

			session.Xaml.Absorb(selection);
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			_gate.Release();
		}

		// From what the app said, not from what was asked: arming can be refused, and a toggle
		// showing a mode the app is not in is worse than one that visibly did not move.
		ShowToggles(session.Xaml);
	}

	private async void OnDeselect(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;

		await _gate.WaitAsync(CancellationToken.None);

		try
		{
			var selection = await client.DeselectXamlAsync(session.SessionId, CancellationToken.None);
			session.Xaml.Absorb(selection);
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			_gate.Release();
		}

		session.Xaml.LastPushedHandle = 0;
		Choose(null);
		Show(session.Xaml);
	}

	/// <summary>
	/// Turns the framework's own defaults on or off, and re-reads the selected element so the list
	/// changes with the toggle rather than at the next selection.
	/// </summary>
	private async void OnToggleIncludeDefaults(object sender, RoutedEventArgs args)
	{
		if (_session is not { } session) return;

		session.Xaml.IncludeDefaults = IncludeDefaultsBox.IsChecked == true;

		if (session.Xaml.Selected is not { } selected) return;

		await ChooseAsync(selected, push: false);
	}

	/// <summary>
	/// Copies the address, which is what a tool that changes this element takes. It is the one thing
	/// on this pane somebody needs to type somewhere else.
	/// </summary>
	private void OnCopyAddress(object sender, RoutedEventArgs args)
	{
		if (_session?.Xaml.Selected?.Address is not { Length: > 0 } address) return;

		var package = new DataPackage();
		package.SetText(address);
		Clipboard.SetContent(package);

		CopyAddressButton.Content = "Copied";
		DispatcherQueue.TryEnqueue(async () =>
		{
			await Task.Delay(TimeSpan.FromSeconds(1.5));
			CopyAddressButton.Content = "Copy";
		});
	}

	/// <summary>Narrows the tree to what the find box holds, as it is typed. The tree is the window's own, so nothing is asked of the app.</summary>
	private void OnFindChanged(object sender, TextChangedEventArgs args)
	{
		if (_session is not { } session) return;

		session.Xaml.FindText = FindBox.Text;
		Show(session.Xaml);
	}

	/// <summary>Narrows the tree to the elements live edits changed this session, or puts it back.</summary>
	private void OnToggleChanged(object sender, RoutedEventArgs args)
	{
		if (_session is not { } session) return;

		session.Xaml.ChangedOnly = ChangedButton.IsChecked == true;
		Show(session.Xaml);
	}

	/// <summary>
	/// Turns the app's marks of what live edits changed on or off. The app's own switch rather than this window's, so
	/// the toggle shows what the app says afterwards: its Changes button may have moved it since.
	/// </summary>
	private async void OnToggleMarkInApp(object sender, RoutedEventArgs args)
	{
		if (_client is not { } client || _session is not { } session) return;

		var wanted = MarkInAppButton.IsChecked == true;

		await _gate.WaitAsync(CancellationToken.None);

		try
		{
			session.Xaml.Absorb(await client.SetXamlChangeMarksAsync(session.SessionId, wanted, CancellationToken.None));
		}
		catch (Exception exception)
		{
			_report?.Invoke(exception);
		}
		finally
		{
			_gate.Release();
		}

		ShowToggles(session.Xaml);
	}

	/// <summary>Asks an agent about the element on screen, with what changed on it.</summary>
	private void OnAskElement(object sender, RoutedEventArgs args)
	{
		if (_session?.Xaml.Selected is not { } selected) return;

		if (SubjectOf(selected) is { } subject) _ask?.Invoke(subject, AskButton);
	}

	/// <summary>Asks an agent about the element a row's context menu was opened on.</summary>
	private void OnAskRow(object sender, RoutedEventArgs args)
	{
		if ((sender as FrameworkElement)?.Tag is not XamlNodeRow row) return;

		if (SubjectOf(row) is { } subject) _ask?.Invoke(subject, this);
	}

	/// <summary>
	/// The note's subject for an element, made when it is asked for: null once the row is no longer in the tree this
	/// pane shows -- another session bound, or the element gone -- so a shortcut pressed later asks about nothing
	/// rather than about an element that is not there.
	/// </summary>
	private NoteSubject? SubjectOf(XamlNodeRow row)
	{
		if (_session is not { } session || !ReferenceEquals(session.Xaml.Row(row.Handle), row)) return null;

		return AskAgent.Element(session.SessionId, row, session.Xaml.ChangesOf(row.Handle));
	}
}
