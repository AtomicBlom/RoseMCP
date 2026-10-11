using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>One note the person sent an agent, with where it has got to and the agent's answers under it.</summary>
public sealed partial class NoteView : UserControl
{
	public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
		nameof(Row), typeof(NoteRow), typeof(NoteView), new PropertyMetadata(null));

	public static readonly DependencyProperty ShowAgentProperty = DependencyProperty.Register(
		nameof(ShowAgent), typeof(bool), typeof(NoteView), new PropertyMetadata(false));

	public NoteView() => InitializeComponent();

	/// <summary>The note shown.</summary>
	public NoteRow? Row
	{
		get => (NoteRow?)GetValue(RowProperty);
		set => SetValue(RowProperty, value);
	}

	/// <summary>Whether the note names the session it went to, which a card already says and the list of gone sessions does not.</summary>
	public bool ShowAgent
	{
		get => (bool)GetValue(ShowAgentProperty);
		set => SetValue(ShowAgentProperty, value);
	}
}
