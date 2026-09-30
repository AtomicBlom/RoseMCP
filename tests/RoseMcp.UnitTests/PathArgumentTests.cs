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
/// <para>
/// Every path here is composed from the running platform's root rather than written out, because
/// this suite runs on Linux as well and <c>C:\checkouts\main</c> there is a relative path with an
/// odd name in it -- which is what these assert about, so a literal would pass for the wrong reason
/// or fail for one.
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
		var paths = Rooted(Absolute("checkouts", "main"));

		using var origin = CallOrigin.Use(Absolute("checkouts", "worktree"));

		(paths.Of(Path.Combine("tests", "Widget.cs"))?.Value).ShouldBe(
			Absolute("checkouts", "worktree", "tests", "Widget.cs"));
	}

	/// <summary>
	/// With no relay in front of it a session says nothing about where it is, and the broker's own
	/// working directory is that session's directory -- the same fact arrived at differently, rather
	/// than a second rule.
	/// </summary>
	[Test]
	public void A_session_that_says_nothing_is_measured_from_the_brokers_own_directory()
	{
		(Rooted(Absolute("checkouts", "main")).Of(Path.Combine("tests", "Widget.cs"))?.Value).ShouldBe(
			Absolute("checkouts", "main", "tests", "Widget.cs"));
	}

	/// <summary>
	/// An absolute path is what the caller said, and they may mean a file outside the checkout they
	/// are calling from. Inferring a path was the failure; accepting one never was.
	/// </summary>
	[Test]
	public void An_absolute_path_is_left_where_the_caller_put_it()
	{
		var paths = Rooted(Absolute("checkouts", "main"));

		using var origin = CallOrigin.Use(Absolute("checkouts", "worktree"));

		(paths.Of(Absolute("elsewhere", "Widget.cs"))?.Value).ShouldBe(Absolute("elsewhere", "Widget.cs"));
	}

	/// <summary>An argument nobody supplied is not a path that failed to resolve.</summary>
	[Test]
	public void An_argument_nobody_sent_stays_absent()
	{
		var paths = Rooted(Absolute("checkouts", "main"));

		paths.Of(null).ShouldBeNull();
		paths.Of("   ").ShouldBeNull();
		paths.Each(null).ShouldBeEmpty();
	}

	/// <summary>
	/// There is no way to get one of these without saying what a relative path is measured from,
	/// which is the whole of the type: the shorter spelling refuses rather than picking a base.
	/// </summary>
	[Test]
	public void A_path_cannot_be_rooted_without_a_base()
	{
		Should.Throw<ArgumentException>(() => RootedPath.Absolute(Path.Combine("tests", "Widget.cs"))).ShouldBeOfType<ArgumentException>();

		Should.Throw<ArgumentException>(
			() => RootedPath.From(Path.Combine("tests", "Widget.cs"), Path.Combine("somewhere", "relative"))).ShouldBeOfType<ArgumentException>();
	}

	/// <summary>
	/// The host's half. A relative path arriving at a worker resolves against its solution's root,
	/// which is a real directory holding a real file of that name -- so the refusal has to happen
	/// before anything looks at disk.
	/// </summary>
	[Test]
	public void A_host_refuses_a_relative_path_and_names_the_argument()
	{
		var relative = Path.Combine("tests", "Widget.cs");

		var refusal = PathArguments.Relative(Arguments(("filePath", relative), ("symbol", "A.B")));

		refusal.ShouldNotBeNull();
		refusal.ShouldStartWith("filePath has to be an absolute path", Case.Sensitive);
		refusal!.ShouldContain(relative, Case.Sensitive);
	}

	/// <summary>One list argument is one rule: rose_format sends several where every other tool sends one.</summary>
	[Test]
	public void A_host_refuses_a_relative_path_inside_a_list()
	{
		PathArguments.Relative(
			Arguments(("filePaths", new[] { Absolute("repo", "A.cs"), "B.cs" }))).ShouldNotBeNull();

		PathArguments.Relative(
			Arguments(("filePaths", new[] { Absolute("repo", "A.cs"), Absolute("repo", "B.cs") }))).ShouldBeNull();
	}

	/// <summary>
	/// An argument with a base of its own is not covered, and must not be: rose_move_type_to_file's
	/// targetPath is measured from the file being split, which the worker knows and the broker does
	/// not, so it travels as the caller wrote it.
	/// </summary>
	[Test]
	public void An_argument_with_a_base_of_its_own_is_left_alone()
	{
		PathArguments.Relative(Arguments(("symbol", "A.B"), ("targetPath", "Widget.cs"))).ShouldBeNull();
	}

	/// <summary>An absolute path on whichever platform is running: a drive root here, / elsewhere.</summary>
	private static string Absolute(params string[] parts) =>
		Path.GetFullPath(Path.Combine([Path.GetPathRoot(AppContext.BaseDirectory)!, .. parts]));

	private static CallerPaths Rooted(string directory) =>
		new(Options.Create(new BrokerOptions { DefaultWorkspaceRoot = directory }));

	/// <summary>
	/// Arguments as they reach a host, built from values rather than parsed from JSON text, so a
	/// path with separators in it needs no escaping to say what it is.
	/// </summary>
	private static IReadOnlyDictionary<string, JsonElement> Arguments(params (string Name, object Value)[] supplied) =>
		supplied.ToDictionary(
			argument => argument.Name,
			argument => JsonSerializer.SerializeToElement(argument.Value),
			StringComparer.Ordinal);
}
