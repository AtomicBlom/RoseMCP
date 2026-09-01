using RoseMcp.Worker.Xaml;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The breakage no C# tool can see. A rename moves every C# reference correctly and leaves the
/// markup pointing at a name that no longer exists -- which compiles, runs, and shows nothing.
/// </summary>
public sealed class XamlReferenceTests
{
	private const string Markup = """
		<UserControl
			x:Class="Ui.Widget"
			xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
			xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
			xmlns:local="using:Ui.Controls">
			<!-- The Title of this control is set below. -->
			<Grid x:Name="Root">
				<TextBlock Text="{Binding Title}" />
				<TextBlock Text="{x:Bind ViewModel.Title, Mode=OneWay}" />
				<Button Click="OnSaved" />
				<local:Crumb />
			</Grid>
		</UserControl>
		""";

	[Theory]
	[InlineData("Title", "binding")]
	[InlineData("Root", "x:Name")]
	[InlineData("OnSaved", "attribute value")]
	[InlineData("Crumb", "element")]
	[InlineData("Widget", "attribute value")]
	public void Finds_each_way_markup_can_name_something(string name, string expectedKind)
	{
		var mentions = XamlReferenceScanner.InText("Widget.xaml", Markup, name).ToArray();

		Assert.NotEmpty(mentions);
		Assert.Contains(mentions, mention => mention.Kind == expectedKind);
	}

	/// <summary>
	/// Both bindings for Title, and the comment mentioning it is not one of them -- anchoring to XAML
	/// syntax rather than to the text is what keeps this from being a noisy grep.
	/// </summary>
	[Fact]
	public void Ignores_a_name_that_is_only_prose()
	{
		var mentions = XamlReferenceScanner.InText("Widget.xaml", Markup, "Title").ToArray();

		Assert.Equal(2, mentions.Length);
		Assert.All(mentions, mention => Assert.Equal("binding", mention.Kind));
		Assert.DoesNotContain(mentions, mention => mention.Text.Contains("<!--", StringComparison.Ordinal));
	}

	[Fact]
	public void Finds_nothing_for_a_name_the_markup_never_mentions()
	{
		Assert.Empty(XamlReferenceScanner.InText("Widget.xaml", Markup, "Absent"));
	}

	/// <summary>
	/// End to end: renaming the class a markup file declares must report the x:Class that still names
	/// the old one. Nothing rewrites it, because nothing here can prove what it refers to -- but a
	/// rename that reports nothing is how it goes unnoticed.
	/// </summary>
	[Fact]
	public async Task Reports_markup_left_behind_by_a_rename()
	{
		using var fixture = FixtureSolution.Copy("XamlStub", "XamlStub.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var path = fixture.Path("XamlStub", "Ui", "Widget.xaml.cs");
		var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
		var index = text.IndexOf("partial class Widget", StringComparison.Ordinal) + "partial class ".Length;
		var before = text[..index];

		var request = new RenameRequest
		{
			FilePath = path,
			Line = before.Count(character => character == '\n') + 1,
			Column = index - before.LastIndexOf('\n'),
			NewName = "Gadget",
			Apply = false,
		};

		var result = await session.MutateAsync(
			(snapshot, token) => RenameService.RenameAsync(snapshot, request, session.NoteSelfWrite, token),
			TestContext.Current.CancellationToken);

		Assert.Equal("Widget", result.OldName);

		var mention = Assert.Single(result.XamlMentions);

		Assert.EndsWith("Widget.xaml", mention.FilePath, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("x:Class", mention.Text, StringComparison.Ordinal);
		Assert.Contains("markup mention", string.Join(" ", result.Notices), StringComparison.Ordinal);
	}
}
