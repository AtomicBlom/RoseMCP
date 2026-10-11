using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>
/// The bar along the bottom of every view: Rose's total, split into the broker, the workers and the live-app hosts,
/// the debugged apps listed apart from it, and the machine's total. Each process in detail on hover.
/// </summary>
public sealed partial class MemoryBar : UserControl
{
	public MemoryBar()
	{
		InitializeComponent();
		Show(MemorySummary.Unknown);
	}

	/// <summary>Shows a sample.</summary>
	public void Show(MemorySummary memory)
	{
		Total.Text = memory.Rose;
		Split.Text = memory.Split;
		Debugged.Text = memory.Debugged;
		Machine.Text = memory.Machine;
		ToolTipService.SetToolTip(Bar, memory.Detail);
	}
}
