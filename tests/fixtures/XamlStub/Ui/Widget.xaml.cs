namespace Ui;

/// <summary>
/// Code-behind with no base type and no InitializeComponent of its own -- both come from the
/// generated half, which is exactly what the design-time build fails to provide.
/// </summary>
public sealed partial class Widget
{
	public Widget() => InitializeComponent();

	public string Caption() => Save.Label;
}
