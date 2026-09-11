using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One value in a stopped frame, and whatever is inside it once somebody asks.
/// <para>
/// Expanded on demand rather than read whole. An object graph has no bound -- a list of widgets each
/// holding a parent pointer is a cycle, and reading it eagerly is a debugger that hangs on a stop it
/// was asked to describe. So a node knows only whether there is something behind it, which the host
/// answers without a read per row, and fetches it when the expander is opened.
/// </para>
/// </summary>
public sealed class VariableNode : Observable
{
	private bool _isLoading;
	private bool _hasUnrealizedChildren;
	private string _loadDetail = string.Empty;
	private bool _hasLoadDetail;

	public VariableNode(LiveVariable variable)
	{
		Name = variable.Name;
		Kind = variable.Kind;
		TypeName = variable.TypeName ?? string.Empty;
		Value = variable.Value ?? (variable.TypeName is { Length: > 0 } type ? $"{{{type}}}" : string.Empty);
		Path = variable.Path;

		_hasUnrealizedChildren = variable.HasChildren;
	}

	/// <summary>The argument, local, field or element name, as source or metadata gives it.</summary>
	public string Name { get; }

	/// <summary>Which of the four it is, so a tree can group or tint by it.</summary>
	public string Kind { get; }

	public string TypeName { get; }

	/// <summary>
	/// What it holds. An object shows as <c>{TypeName}</c> rather than its <c>ToString</c>, because
	/// that would mean running the debuggee's own code inside a stop taken to inspect it.
	/// </summary>
	public string Value { get; }

	/// <summary>How to address it again, which is what expanding it sends.</summary>
	public string Path { get; }

	/// <summary>What is inside it, empty until it has been expanded.</summary>
	public ObservableCollection<VariableNode> Children { get; } = [];

	/// <summary>
	/// Whether there is something behind this node that has not been fetched. It is what puts an
	/// expander on the row, and it goes false once the fetch lands so the tree does not offer to
	/// expand something it has already opened.
	/// </summary>
	public bool HasUnrealizedChildren
	{
		get => _hasUnrealizedChildren;
		private set => Set(ref _hasUnrealizedChildren, value);
	}

	/// <summary>Whether a fetch for this node is outstanding.</summary>
	public bool IsLoading
	{
		get => _isLoading;
		private set => Set(ref _isLoading, value);
	}

	/// <summary>
	/// What the host said about the expansion that the children do not: a value with more elements
	/// than were returned, or a refusal. Empty when there is nothing to say.
	/// </summary>
	public string LoadDetail
	{
		get => _loadDetail;
		private set => Set(ref _loadDetail, value);
	}

	public bool HasLoadDetail
	{
		get => _hasLoadDetail;
		private set => Set(ref _hasLoadDetail, value);
	}

	/// <summary>Marks a fetch as started, so the row can say it is working and nothing asks twice.</summary>
	public void Loading()
	{
		IsLoading = true;
		LoadDetail = string.Empty;
		HasLoadDetail = false;
	}

	/// <summary>
	/// Takes what was inside the value.
	/// <para>
	/// The expander goes away whatever came back, including nothing. A node that kept offering to
	/// expand after an empty answer is one a reader clicks repeatedly, and every click is another
	/// read of a value the host has already said holds nothing they can see.
	/// </para>
	/// </summary>
	public void Fill(LiveValueExpansion expansion)
	{
		Children.Clear();
		foreach (var child in expansion.Children)
		{
			Children.Add(new VariableNode(child));
		}

		IsLoading = false;
		HasUnrealizedChildren = false;
		LoadDetail = Describe(expansion);
		HasLoadDetail = LoadDetail.Length > 0;
	}

	/// <summary>
	/// Says why nothing came back. The expander stays, because the reason is usually the stop having
	/// ended rather than the value being empty, and the next stop is worth another try.
	/// </summary>
	public void Failed(string detail)
	{
		IsLoading = false;
		LoadDetail = detail;
		HasLoadDetail = detail.Length > 0;
	}

	/// <summary>
	/// What an expansion says beyond its children. A truncated one is the case that matters: a list
	/// showing a hundred of its four thousand elements looks exactly like a list of a hundred.
	/// </summary>
	private static string Describe(LiveValueExpansion expansion)
	{
		if (expansion.Detail is { Length: > 0 } detail) return detail;

		return expansion.Truncated
			? $"showing {expansion.Children.Count} of {expansion.Total}"
			: string.Empty;
	}
}
