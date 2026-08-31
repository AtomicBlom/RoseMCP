// Stands in for Windows.UI.Xaml so this fixture needs no Windows SDK and no UWP tooling. The stub
// generator resolves element types out of the compilation, and does not care whether the framework
// came from a reference or from source.
namespace Windows.UI.Xaml
{
	public class DependencyObject
	{
	}

	public class FrameworkElement : DependencyObject
	{
	}
}

namespace Windows.UI.Xaml.Controls
{
	public class Control : Windows.UI.Xaml.FrameworkElement
	{
	}

	public class UserControl : Control
	{
	}

	public class Button : Control
	{
		public string Label { get; set; } = string.Empty;
	}

	public class Grid : Control
	{
	}
}
