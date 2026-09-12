using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// Turning the names metadata records back into the ones somebody wrote.
/// <para>
/// It matters most exactly where a reader is least able to check it. Offering a breakpoint in
/// <c>&lt;&gt;c.&lt;Refresh&gt;b__3_0</c> tells them they picked something other than the line they
/// clicked, and they have no way to find out that they did not.
/// </para>
/// </summary>
public sealed class MethodDisplayNameTests
{
	[Test]
	[Arguments("MyApp.Widget", "Refresh", "Widget.Refresh")]
	[Arguments("MyApp.Ui.Widget", "Refresh", "Widget.Refresh")]
	[Arguments("MyApp.Outer+Inner", "Refresh", "Outer.Inner.Refresh")]
	[Arguments("MyApp.Cache`1", "Get", "Cache.Get")]
	[Arguments("MyApp.Widget", ".ctor", "Widget (constructor)")]
	[Arguments("MyApp.Widget", ".cctor", "Widget (static constructor)")]
	[Arguments("MyApp.Widget", "get_Title", "Widget.Title (get)")]
	[Arguments("MyApp.Widget", "set_Title", "Widget.Title (set)")]
	[Arguments("MyApp.Widget", "add_Changed", "Widget.Changed (add)")]
	[Arguments("MyApp.Widget", "remove_Changed", "Widget.Changed (remove)")]
	public void A_method_reads_as_a_person_wrote_it(string typeName, string methodName, string expected)
	{
		Assert.Equal(expected, MethodDisplayName.Of(typeName, methodName));
	}

	/// <summary>
	/// The three shapes a breakpoint inside a method body actually binds to. Each is named for the
	/// method it was written in, because that is the only name the reader has ever seen.
	/// </summary>
	[Test]
	[Arguments("MyApp.Widget+<>c", "<Refresh>b__3_0", "Widget.Refresh (lambda)")]
	[Arguments("MyApp.Widget+<>c__DisplayClass3_0", "<Refresh>b__0", "Widget.Refresh (lambda)")]
	[Arguments("MyApp.Widget", "<Refresh>g__Inner|3_0", "Widget.Refresh > Inner (local function)")]
	[Arguments("MyApp.Widget+<Refresh>d__3", "MoveNext", "Widget.Refresh (async or iterator)")]
	public void What_the_compiler_wrote_is_named_for_the_method_it_came_from(
		string typeName,
		string methodName,
		string expected)
	{
		Assert.Equal(expected, MethodDisplayName.Of(typeName, methodName));
	}

	/// <summary>
	/// Which methods a name search offers. The generated ones are reachable by picking a line, and a
	/// list of things to break in is a list of things somebody wrote.
	/// </summary>
	[Test]
	public void A_generated_method_is_not_something_to_search_by_name()
	{
		Assert.True(MethodDisplayName.IsCompilerGenerated("MyApp.Widget+<>c", "<Refresh>b__3_0"));
		Assert.True(MethodDisplayName.IsCompilerGenerated("MyApp.Widget+<Refresh>d__3", "MoveNext"));
		Assert.True(MethodDisplayName.IsCompilerGenerated("MyApp.Widget", "<Refresh>g__Inner|3_0"));

		Assert.False(MethodDisplayName.IsCompilerGenerated("MyApp.Widget", "Refresh"));
		Assert.False(MethodDisplayName.IsCompilerGenerated("MyApp.Widget", "get_Title"));
	}
}
