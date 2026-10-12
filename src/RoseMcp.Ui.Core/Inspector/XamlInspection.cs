using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// What a reader is looking at in a live app's visual tree: the tree itself, which element is
/// selected, and that element's properties.
/// <para>
/// Per session and kept across reads, unlike a stop's inspection. A handle outlives a read -- it is
/// stable for the life of the element -- so a refresh brings rows in line rather than replacing
/// them, and the expansion and selection somebody set survive it. That matters most right after a
/// live edit, which is when the tree is re-read and when a reader is watching one element.
/// </para>
/// </summary>
public sealed class XamlInspection : Observable
{
	private readonly Dictionary<ulong, int> _reads = [];

	private XamlNodeRow? _selected;
	private bool _armed;
	private bool _justMyXaml = true;
	private bool _includeDefaults;
	private string _treeDetail = string.Empty;
	private bool _hasTreeDetail;
	private string _propertiesDetail = string.Empty;
	private bool _hasPropertiesDetail;
	private string _observedNote = string.Empty;
	private string _findText = string.Empty;
	private bool _changedOnly;
	private bool _changeMarks = true;
	private int _changedCount;
	private string _filterNote = string.Empty;
	private DateTime? _asOfUtc;

	/// <summary>The live edits this session made, by the element they landed on.</summary>
	private Dictionary<ulong, List<LiveXamlChange>> _changes = [];

	/// <summary>The selected element's properties as last read, kept so a change landing re-marks them without reading the app.</summary>
	private IReadOnlyList<LiveXamlProperty> _readProperties = [];

	/// <summary>Every element by handle, which is how a pick in the app finds its row.</summary>
	internal Dictionary<ulong, XamlNodeRow> Index { get; private set; } = [];

	/// <summary>The top of the tree. More than one is ordinary: an app can have several windows.</summary>
	public ObservableCollection<XamlNodeRow> Roots { get; } = [];

	/// <summary>The selected element's properties, what set each, and in that order.</summary>
	public ObservableCollection<PropertyRow> Properties { get; } = [];

	public int Count => Index.Count;

	/// <summary>The element whose properties are shown, or null when nothing is selected.</summary>
	public XamlNodeRow? Selected
	{
		get => _selected;
		private set => Set(ref _selected, value);
	}

	/// <summary>
	/// The handle this window last asked the app to select, so the answer to its own request is not
	/// mistaken for somebody clicking in the app.
	/// <para>
	/// Needed because the selection is read from the app rather than remembered -- the person can
	/// pick and cancel without this side being told, so the app is the truth. The cost is that our
	/// own push comes back looking exactly like a pick, and acting on it would reveal and re-select
	/// the element that was already selected, once a second, forever.
	/// </para>
	/// </summary>
	public ulong LastPushedHandle { get; set; }

	/// <summary>
	/// Whether select mode is armed in the app, read from the app rather than remembered because
	/// its own toolbar can arm and cancel it.
	/// </summary>
	public bool Armed
	{
		get => _armed;
		private set => Set(ref _armed, value);
	}

	/// <summary>Whether picks prefer the app's own markup over a control template's parts.</summary>
	public bool JustMyXaml
	{
		get => _justMyXaml;
		private set => Set(ref _justMyXaml, value);
	}

	/// <summary>Whether the framework's defaults are asked for along with what is set.</summary>
	public bool IncludeDefaults
	{
		get => _includeDefaults;
		set => Set(ref _includeDefaults, value);
	}

	/// <summary>Why the tree is empty or partial, when it is. Empty when there is nothing to say.</summary>
	public string TreeDetail
	{
		get => _treeDetail;
		private set => Set(ref _treeDetail, value);
	}

	public bool HasTreeDetail
	{
		get => _hasTreeDetail;
		private set => Set(ref _hasTreeDetail, value);
	}

	/// <summary>Why an element's properties could not be read, when they could not.</summary>
	public string PropertiesDetail
	{
		get => _propertiesDetail;
		private set => Set(ref _propertiesDetail, value);
	}

	public bool HasPropertiesDetail
	{
		get => _hasPropertiesDetail;
		private set => Set(ref _hasPropertiesDetail, value);
	}

	/// <summary>
	/// That reading an element changes what it reports about itself, and how many times this one has
	/// been read. Said rather than filtered: hiding the properties a read materialised would hide
	/// exactly what an apply-then-read-back loop exists to verify.
	/// </summary>
	public string ObservedNote
	{
		get => _observedNote;
		private set => Set(ref _observedNote, value);
	}

	/// <summary>Text the tree is narrowed to: rows whose name, type, address or file hold it, and the way down to them.</summary>
	public string FindText
	{
		get => _findText;
		set
		{
			var text = value ?? string.Empty;
			if (text == _findText) return;

			Set(ref _findText, text);
			Refilter();
		}
	}

	/// <summary>Whether the tree is narrowed to the elements live edits changed this session.</summary>
	public bool ChangedOnly
	{
		get => _changedOnly;
		set
		{
			if (value == _changedOnly) return;

			Set(ref _changedOnly, value);
			Refilter();
		}
	}

	/// <summary>
	/// Whether the app marks the elements live edits changed, read from the app because its toolbar's Changes button
	/// and this window's toggle are one switch.
	/// </summary>
	public bool ChangeMarks
	{
		get => _changeMarks;
		private set => Set(ref _changeMarks, value);
	}

	/// <summary>How many elements in the tree a live edit changed this session, which the Changed filter is labelled with.</summary>
	public int ChangedCount
	{
		get => _changedCount;
		private set => Set(ref _changedCount, value);
	}

	/// <summary>What the filters left of the tree, said where it is not the whole of it; empty otherwise.</summary>
	public string FilterNote
	{
		get => _filterNote;
		private set => Set(ref _filterNote, value);
	}

	/// <summary>
	/// When the tree on screen was read, where it is not a fresh read: the target is stopped and this is the last tree
	/// read from it. Null for a fresh read.
	/// </summary>
	public DateTime? AsOfUtc
	{
		get => _asOfUtc;
		private set => Set(ref _asOfUtc, value);
	}

	/// <summary>The live edits that landed on an element this session, oldest first; empty where none did.</summary>
	public IReadOnlyList<LiveXamlChange> ChangesOf(ulong handle) => _changes.TryGetValue(handle, out var changes) ? changes : [];

	/// <summary>The row for a handle, or null when this tree does not have it.</summary>
	public XamlNodeRow? Row(ulong handle) => Index.GetValueOrDefault(handle);

	/// <summary>
	/// Takes the record of live edits made this session: marks each changed element's row, narrows the tree again where
	/// it shows only what changed, and puts each change beside its property for the element on screen. Read from the
	/// broker rather than the app, so it is taken while the target is stopped as well.
	/// </summary>
	public void Absorb(LiveXamlChangeList list)
	{
		ArgumentNullException.ThrowIfNull(list);

		_changes = list.Changes
			.Where(change => change.Handle is not null)
			.GroupBy(change => change.Handle!.Value)
			.ToDictionary(group => group.Key, group => group.OrderBy(change => change.Sequence).ToList());

		Mark();
		Refilter();

		if (Selected is not null) ShowProperties();
	}

	/// <summary>
	/// Shows the selected element's live edits in place of its properties, for a target that is stopped and so cannot be
	/// read: what changed is the broker's record, and still true; what the rest of the element says is not knowable now.
	/// </summary>
	public void ShowChangesOnly(string why)
	{
		if (Selected is not { } selected) return;

		_readProperties = [];
		Properties.Clear();
		foreach (var row in PropertyRow.Sort([], ChangesOf(selected.Handle)))
		{
			Properties.Add(row);
		}

		PropertiesDetail = why;
		HasPropertiesDetail = why.Length > 0;
		ObservedNote = string.Empty;
	}

	/// <summary>Marks each row a live edit changed, and counts them for the Changed filter's label.</summary>
	private void Mark()
	{
		var changed = 0;

		foreach (var (handle, row) in Index)
		{
			var changes = ChangesOf(handle);
			row.IsChanged = changes.Count > 0;
			row.ChangeNote = changes.Count > 0 ? XamlChangeText.Note(changes) : string.Empty;

			if (row.IsChanged) changed++;
		}

		ChangedCount = changed;
	}

	/// <summary>Narrows the tree to the filters as they stand, and says what that left where it is not everything.</summary>
	private void Refilter()
	{
		var query = new XamlTreeQuery(FindText, ChangedOnly, JustMyXaml);
		var filtered = XamlTreeFilter.Apply(this, query);

		FilterNote = InspectorText.Filtered(query, filtered, Count);
	}

	/// <summary>The selected element's properties as last read, with this session's changes beside them.</summary>
	private void ShowProperties()
	{
		if (Selected is not { } selected) return;

		Properties.Clear();
		foreach (var property in PropertyRow.Sort(_readProperties, ChangesOf(selected.Handle)))
		{
			Properties.Add(property);
		}
	}

	/// <summary>How many times this window has read an element's properties.</summary>
	public int Reads(ulong handle) => _reads.GetValueOrDefault(handle);

	/// <summary>Takes a fresh read of the tree, keeping every row the new one still has.</summary>
	public void Absorb(LiveXamlTree tree)
	{
		XamlTreeBuilder.Merge(this, tree.Nodes);

		TreeDetail = Describe(tree);
		HasTreeDetail = TreeDetail.Length > 0;
		AsOfUtc = tree.AsOfUtc;

		// A row that has just appeared is marked and filtered like the rest, rather than arriving unmarked and on show
		// whatever the filters say.
		Mark();
		Refilter();
	}

	/// <summary>Takes what the builder worked out, and drops a selection the tree no longer has.</summary>
	internal void Reindex(Dictionary<ulong, XamlNodeRow> index)
	{
		Index = index;

		if (Selected is not { } selected || index.ContainsKey(selected.Handle)) return;

		// The element has gone. Said rather than left showing the properties of something that is
		// not there any more, which is the shape of wrong this whole pane is built to avoid.
		Selected = null;
		Properties.Clear();
		PropertiesDetail = "The element that was selected is not in the tree any more.";
		HasPropertiesDetail = true;
		ObservedNote = string.Empty;
	}

	/// <summary>
	/// Moves the selection, which is what decides whose properties are shown.
	/// <para>
	/// One record of it, here. A flag on the row as well would be a second, and the tree control keeps
	/// its own -- three copies of one fact, which is how a row ends up highlighted while the pane
	/// beside it says nothing is selected.
	/// </para>
	/// </summary>
	public void Select(XamlNodeRow? row)
	{
		if (ReferenceEquals(row, Selected)) return;

		Selected = row;
		_readProperties = [];
		Properties.Clear();
		PropertiesDetail = string.Empty;
		HasPropertiesDetail = false;
		ObservedNote = string.Empty;
	}

	/// <summary>
	/// Takes an element's properties, for the element they were read for. Dropped when the selection
	/// has moved on: a read is a round trip and a reader clicking down a tree starts several, so
	/// without this the last one to arrive wins rather than the last one asked for.
	/// </summary>
	public void Absorb(LiveXamlProperties properties)
	{
		if (Selected is not { } selected || selected.Handle != properties.Handle) return;

		_reads[properties.Handle] = Reads(properties.Handle) + 1;
		_readProperties = properties.Properties;

		ShowProperties();

		PropertiesDetail = properties.Detail ?? string.Empty;
		HasPropertiesDetail = PropertiesDetail.Length > 0;
		ObservedNote = InspectorText.PropertiesAreObserved(Reads(properties.Handle));
	}

	/// <summary>
	/// Takes what the app says about select mode. The toggles follow the app rather than the other
	/// way round, because the person can arm and cancel from its own toolbar.
	/// </summary>
	public void Absorb(LiveXamlSelection selection)
	{
		Armed = selection.Armed;
		ChangeMarks = selection.ChangeMarks;

		// Just my XAML narrows the tree as well as the pick, so the two cannot disagree about which elements are the
		// app's: the tree is narrowed again when the app's own toggle moves.
		if (selection.JustMyXaml == JustMyXaml) return;

		JustMyXaml = selection.JustMyXaml;
		Refilter();
	}

	/// <summary>
	/// Whether this is somebody picking in the running app, rather than the app answering a push
	/// from this window or repeating a selection already on screen.
	/// </summary>
	public bool PickedInApp(LiveXamlSelection selection) =>
		selection.Selected
		&& selection.Handle != 0
		&& selection.Handle != LastPushedHandle
		&& selection.Handle != Selected?.Handle;

	/// <summary>
	/// What a tree does not say for itself: why it is empty, or that it is only part of one. A page
	/// shorter than the total is the case worth a sentence, since a truncated tree reads as a
	/// complete one with a surprisingly small app in it.
	/// </summary>
	private static string Describe(LiveXamlTree tree)
	{
		if (tree.Detail is { Length: > 0 } detail) return detail;

		return tree.Total > tree.Count
			? $"Showing {tree.Count} of {tree.Total} elements."
			: string.Empty;
	}
}
