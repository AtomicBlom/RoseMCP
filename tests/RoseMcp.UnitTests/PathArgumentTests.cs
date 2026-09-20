using System.Text.Json;

using Microsoft.Extensions.Options;

using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Where a relative path argument is measured from, and what happens to one that reaches a host
/// still relative.
/// <para>
/// The failure these are about is the only one on this surface a caller cannot see: a path meant
/// for one checkout, resolved against another, names a real file there and is written to, and the
/// result reads as a success while the caller's own working copy stays clean.
/// </para>
/// </summary>
public sealed class PathArgumentTests
{
	/// <summary>
	/// The broker's half. Two checkouts hold the same relative path, and the one the caller is
	/// standing in is the one that wins -- not the one the broker process happens to be sitting in.
	/// </summary>
	[Test]
	public void A_relative_path_is_measured_from_the_calling_session()
	{
		var paths = Rooted(@"C:\checkouts\main");

		using var origin = CallOrigin.Use(@"C:\checkouts\worktree");

		Assert.Equal(@"C:\checkouts\worktree\tests\Widget.cs", paths.Of(@"tests\Widget.cs")?.Value);
	}

	/// <summary>
	/// With no relay in front of it a session says nothing about where it is, and the broker's own
	/// working directory is that session's directory -- the same fact arrived at differently, rather
	/// than a second rule.
	/// </summary>
	[Test]
	public void A_session_that_says_nothing_is_measured_from_the_brokers_own_directory()
	{
		Assert.Equal(@"C:\checkouts\main\tests\Widget.cs", Rooted(@"C:\checkouts\main").Of(@"tests\Widget.cs")?.Value);
	}

	/// <summary>
	/// An absolute path is what the caller said, and they may mean a file outside the checkout they
	/// are calling from. Inferring a path was the failure; accepting one never was.
	/// </summary>
	[Test]
	public void An_absolute_path_is_left_where_the_caller_put_it()
	{
		var paths = Rooted(@"C:\checkouts\main");

		using var origin = CallOrigin.Use(@"C:\checkouts\worktree");

		Assert.Equal(@"C:\elsewhere\Widget.cs", paths.Of(@"C:\elsewhere\Widget.cs")?.Value);
	}

	/// <summary>An argument nobody supplied is not a path that failed to resolve.</summary>
	[Test]
	public void An_argument_nobody_sent_stays_absent()
	{
		var paths = Rooted(@"C:\checkouts\main");

		Assert.Null(paths.Of(null));
		Assert.Null(paths.Of("   "));
		Assert.Empty(paths.Each(null));
	}

	/// <summary>
	/// There is no way to get one of these without saying what a relative path is measured from,
	/// which is the whole of the type: the shorter spelling refuses rather than picking a base.
	/// </summary>
	[Test]
	public void A_path_cannot_be_rooted_without_a_base()
	{
		Assert.Throws<ArgumentException>(() => RootedPath.Absolute(@"tests\Widget.cs"));
		Assert.Throws<ArgumentException>(() => RootedPath.From(@"tests\Widget.cs", "somewhere-relative"));
	}

	/// <summary>
	/// The host's half. A relative path arriving at a worker resolves against its solution's root,
	/// which is a real directory holding a real file of that name -- so the refusal has to happen
	/// before anything looks at disk.
	/// </summary>
	[Test]
	public void A_host_refuses_a_relative_path_and_names_the_argument()
	{
		var refusal = PathArguments.Relative(Arguments("""{"filePath":"tests/Widget.cs","symbol":"A.B"}"""));

		Assert.NotNull(refusal);
		Assert.StartsWith("filePath has to be an absolute path", refusal, StringComparison.Ordinal);
		Assert.Contains("tests/Widget.cs", refusal!, StringComparison.Ordinal);
	}

	/// <summary>One list argument is one rule: rose_format sends several where every other tool sends one.</summary>
	[Test]
	public void A_host_refuses_a_relative_path_inside_a_list()
	{
		Assert.NotNull(PathArguments.Relative(
			Arguments("""{"filePaths":["C:/repo/A.cs","B.cs"]}""")));

		Assert.Null(PathArguments.Relative(
			Arguments("""{"filePaths":["C:/repo/A.cs","C:/repo/B.cs"]}""")));
	}

	/// <summary>
	/// An argument with a base of its own is not covered, and must not be: rose_move_type_to_file's
	/// targetPath is measured from the file being split, which the worker knows and the broker does
	/// not, so it travels as the caller wrote it.
	/// </summary>
	[Test]
	public void An_argument_with_a_base_of_its_own_is_left_alone()
	{
		Assert.Null(PathArguments.Relative(Arguments("""{"symbol":"A.B","targetPath":"Widget.cs"}""")));
	}

	private static CallerPaths Rooted(string directory) =>
		new(Options.Create(new BrokerOptions { DefaultWorkspaceRoot = directory }));

	private static IReadOnlyDictionary<string, JsonElement> Arguments(string json) =>
		JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(
			property => property.Name,
			property => property.Value,
			StringComparer.Ordinal);
}
