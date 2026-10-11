using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RoseMcp.Ui.Core.Inspector;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.App.Views;

/// <summary>
/// A note to an agent about the thing it sits under -- a call, a workspace's reason -- written in place and sent
/// through the app's client. The row it is on says what the note is about and whom it can go to; this only shows
/// that and sends.
/// </summary>
public sealed partial class NoteComposerView : UserControl
{
	public static readonly DependencyProperty ComposerProperty = DependencyProperty.Register(
		nameof(Composer), typeof(NoteComposer), typeof(NoteComposerView), new PropertyMetadata(null));

	public static readonly DependencyProperty OpenLabelProperty = DependencyProperty.Register(
		nameof(OpenLabel), typeof(string), typeof(NoteComposerView), new PropertyMetadata("Ask the agent about this…"));

	public NoteComposerView() => InitializeComponent();

	/// <summary>The note being written.</summary>
	public NoteComposer? Composer
	{
		get => (NoteComposer?)GetValue(ComposerProperty);
		set => SetValue(ComposerProperty, value);
	}

	/// <summary>What the link that opens the composer says.</summary>
	public string OpenLabel
	{
		get => (string)GetValue(OpenLabelProperty);
		set => SetValue(OpenLabelProperty, value);
	}

	private static OperatorClient Client => ((App)Application.Current).Client;

	private void OnToggle(object sender, RoutedEventArgs args) => Composer?.Toggle();

	private async void OnSend(object sender, RoutedEventArgs args)
	{
		if (Composer is not { } composer) return;

		try
		{
			await composer.SendAsync(Client.SendNoteAsync, CancellationToken.None);
		}
		catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
		{
			// Nothing cancels this send but the app ending, which takes the client with it.
		}
	}
}
