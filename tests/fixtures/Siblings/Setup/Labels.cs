using Shared;

namespace Setup;

/// <summary>
/// In the installer solution only, so a rename made through the main one rewrites the declaration
/// this calls and leaves this call on the old name.
/// </summary>
public sealed class Labels
{
	public string For(Widget widget) => widget.Describe();
}