using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One element of a live app's visual tree, as a row a reader can expand and select.
/// <para>
/// Updated in place rather than rebuilt, unlike a stack frame. A frame dies with its stop; an
/// element outlives every read of the tree it is in, and the handle says so. Rebuilding would close
/// every expander and drop the selection on each refresh -- and a refresh is what happens after every
/// live edit, which is exactly when a reader is watching one element.
/// </para>
/// </summary>
public sealed class XamlNodeRow : Observable
{
	private string _label = string.Empty;
	private string _typeName = string.Empty;
	private string _where = string.Empty;
	private bool _hasWhere;
	private bool _isExpanded;
	private bool _isOrphan;
	private bool _isShown = true;
	private bool _isChanged;
	private string _changeNote = string.Empty;

	public XamlNodeRow(LiveXamlNode node)
	{
		Handle = node.Handle;
		Update(node);
	}

	/// <summary>
	/// The diagnostics handle, stable for the life of the element. It is the row's identity, which
	/// is what lets a refresh keep the expansion and the selection somebody set.
	/// </summary>
	public ulong Handle { get; }

	/// <summary>What the element is called: <c>Border</c>, or <c>Border #Frame</c> when it is named.</summary>
	public string Label
	{
		get => _label;
		private set => Set(ref _label, value);
	}

	public string TypeName
	{
		get => _typeName;
		private set => Set(ref _typeName, value);
	}

	/// <summary>The element's <c>x:Name</c>, when the markup gave it one.</summary>
	public string? Name { get; private set; }

	/// <summary>
	/// How to name this element to a tool that changes one. Null when the tree could not compute one,
	/// which leaves the element readable and not addressable.
	/// </summary>
	public string? Address { get; private set; }

	/// <summary>The markup that declared it, as <c>MainPage.xaml:31</c>. Empty when nothing said.</summary>
	public string Where
	{
		get => _where;
		private set => Set(ref _where, value);
	}

	public bool HasWhere
	{
		get => _hasWhere;
		private set => Set(ref _hasWhere, value);
	}

	/// <summary>The element's children, in the order the framework holds them.</summary>
	public ObservableCollection<XamlNodeRow> Children { get; } = [];

	/// <summary>What this element hangs off, or null at a root. Set by the builder, and what walks up.</summary>
	public XamlNodeRow? Parent { get; internal set; }

	public bool IsExpanded
	{
		get => _isExpanded;
		set => Set(ref _isExpanded, value);
	}

	/// <summary>
	/// Whether this element names a parent the tree cannot place it under -- absent from the snapshot,
	/// or an ancestor of itself -- so it is shown at the top instead.
	/// <para>
	/// Said rather than hidden. An element with a parent nobody listed is the snapshot disagreeing with
	/// itself, and dropping it would turn that into a tree quietly missing a subtree, which reads as a
	/// complete tree and is the one failure a reader cannot spot.
	/// </para>
	/// </summary>
	public bool IsOrphan
	{
		get => _isOrphan;
		internal set => Set(ref _isOrphan, value);
	}

	/// <summary>
	/// Whether the filters above the tree leave this row on screen: it matches them, or something under it does, so
	/// the way down to a match stays. True whenever no filter is on.
	/// </summary>
	public bool IsShown
	{
		get => _isShown;
		internal set => Set(ref _isShown, value);
	}

	/// <summary>Whether a live edit this session changed the element, which the row is marked for.</summary>
	public bool IsChanged
	{
		get => _isChanged;
		internal set => Set(ref _isChanged, value);
	}

	/// <summary>What the edits changed on it, one property a line, as its mark's tooltip says it; empty when nothing did.</summary>
	public string ChangeNote
	{
		get => _changeNote;
		internal set => Set(ref _changeNote, value);
	}

	/// <summary>
	/// The markup file the element was declared in, as the app's source info names it -- an <c>ms-appx:</c> or
	/// <c>ms-resource:</c> URI -- or null when it carries none. What Just my XAML decides on.
	/// </summary>
	public string? File { get; private set; }

	/// <summary>Brings the row in line with a fresh read of the same element.</summary>
	public void Update(LiveXamlNode node)
	{
		TypeName = node.TypeName;
		Name = node.Name;
		Address = node.Address;
		File = node.File;
		Label = Describe(ShortNameOf(node.TypeName), node.Name);

		Where = Format.FileLine(node.File, node.Line);
		HasWhere = Where.Length > 0;
	}

	/// <summary>
	/// The element in one line. The name carries a <c>#</c> because that is how a name is spelled to
	/// every tool that takes one, so what a reader sees is what they would type.
	/// </summary>
	public static string Describe(string typeName, string? name) =>
		name is { Length: > 0 } named ? $"{typeName} #{named}" : typeName;

	/// <summary>
	/// A type without its namespace, which is what a tree row can show.
	/// <para>
	/// A WinUI tree is almost entirely <c>Microsoft.UI.Xaml.Controls</c>, so qualifying every row spends
	/// the column on a prefix every row shares and then truncates the one word that differs -- which is
	/// a list of elements nobody can tell apart. The full name is still on
	/// <see cref="TypeName"/>, and is what the element's own header shows.
	/// </para>
	/// </summary>
	public static string ShortNameOf(string typeName)
	{
		var dot = typeName.LastIndexOf('.');

		return dot >= 0 && dot < typeName.Length - 1 ? typeName[(dot + 1)..] : typeName;
	}
}
