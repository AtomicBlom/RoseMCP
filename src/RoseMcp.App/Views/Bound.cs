using Microsoft.UI.Xaml;

namespace RoseMcp.App.Views;

/// <summary>What a control inside an item's template was made for.</summary>
internal static class Bound
{
	/// <summary>
	/// The item <paramref name="sender"/> carries in its <c>Tag</c>, which each template binds to its item.
	/// <para>
	/// The Tag rather than the data context, because a compiled template binds through generated code that does not
	/// need a data context, and inside an <c>ItemsRepeater</c> a handler reading one is handed nothing to act on --
	/// a button that silently does nothing.
	/// </para>
	/// </summary>
	public static T? Item<T>(object sender)
		where T : class => sender is FrameworkElement { Tag: T item } ? item : null;
}
