using Shared;

namespace App;

/// <summary>In the main solution only, so the installer solution never sees this call site.</summary>
public sealed class Uses
{
	public string Ask(Widget widget) => widget.Describe();
}