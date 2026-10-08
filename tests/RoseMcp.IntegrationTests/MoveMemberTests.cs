using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.TestSupport;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// Moving a member between types. One tool rather than an add and a delete, because those are two
/// writes and a failure between them leaves the member declared twice -- and because the call sites
/// are the part a person doing it by hand forgets, choosing once per file and differently each time.
/// </summary>
public sealed class MoveMemberTests
{
	[Test]
	public async Task Moves_a_member_and_qualifies_its_call_sites()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Regioned.Twice", "Library.Greeter");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var source = await ReadAsync(fixture, "Regioned.cs");
		var target = await ReadAsync(fixture, "Greeter.cs");
		var caller = await ReadAsync(fixture, "Builds.cs");

		source.ShouldNotContain("Twice", Case.Sensitive);
		target.ShouldContain("public static int Twice(int value)", Case.Sensitive);

		// The call site in another file now names the new home, which is the half a person forgets.
		caller.ShouldContain("Greeter.Twice(21)", Case.Sensitive);
		string.Join(" ", result.Notices).ShouldContain("call site(s) now name Greeter", Case.Sensitive);
	}

	/// <summary>
	/// A member its own file calls above its declaration comes out of the type it left. Qualifying
	/// those calls moves every position under them, so the declaration is not where it stood when the
	/// move found it -- and a move that then leaves it there compiles, because the two types differ,
	/// so nothing downstream says the member is now declared twice.
	/// </summary>
	[Test]
	public async Task Removes_a_member_its_own_file_calls_above_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Relocated.Helper", "Library.Greeter");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var source = await ReadAsync(fixture, "Relocated.cs");
		var target = await ReadAsync(fixture, "Greeter.cs");

		source.ShouldContain("Greeter.Helper(1) + Greeter.Helper(2)", Case.Sensitive);
		source.ShouldNotContain("public static int Helper", Case.Sensitive);
		target.ShouldContain("public static int Helper(int value)", Case.Sensitive);
	}

	/// <summary>
	/// The same from the other side: a type whose file calls the member above it is still found to
	/// move into, once qualifying that call has moved it.
	/// </summary>
	[Test]
	public async Task Moves_into_a_type_its_own_file_calls_the_member_above()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Relocated.Helper", "Library.Resettled");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var file = await ReadAsync(fixture, "Relocated.cs");
		var declaration = file.IndexOf("public static int Helper(int value)", StringComparison.Ordinal);

		file.ShouldContain("Resettled.Helper(1) + Resettled.Helper(2)", Case.Sensitive);
		file.LastIndexOf("public static int Helper(int value)", StringComparison.Ordinal).ShouldBe(declaration);
		declaration.ShouldBeGreaterThan(file.IndexOf("class Resettled", StringComparison.Ordinal));
	}

	/// <summary>
	/// The documentation comment goes with the declaration. Leaving it behind would leave a summary
	/// describing something that is not there, above whatever the next member turns out to be.
	/// </summary>
	[Test]
	public async Task Takes_the_documentation_comment_with_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		await MoveAsync(session, "Library.Regioned.Thrice", "Library.Greeter");

		var source = await ReadAsync(fixture, "Regioned.cs");
		var target = await ReadAsync(fixture, "Greeter.cs");

		source.ShouldNotContain("Trebles it", Case.Sensitive);
		target.ShouldContain("Trebles it", Case.Sensitive);

		// The region it left is still balanced.
		source.ShouldContain("#region Helpers", Case.Sensitive);
		source.ShouldContain("#endregion", Case.Sensitive);
	}

	/// <summary>
	/// A hand-wrapped signature keeps its shape through a move, re-indented for the type it lands in
	/// rather than deepened by it.
	/// <para>
	/// The member arrives as its own source text with the first line's indentation trimmed off, which
	/// reads as a baseline of nothing while every continuation still carries the old type's. The
	/// destination's indentation then goes on top of indentation that is already there, and the list
	/// lands a level deeper than the member it belongs to. Nothing downstream reports it: a
	/// continuation line is not a statement, so Roslyn's formatter has no rule that moves one, and
	/// neither IDE0055 nor <c>dotnet format</c> has an opinion about where a wrapped list sits.
	/// </para>
	/// </summary>
	[Test]
	public async Task Keeps_the_shape_of_a_wrapped_signature_it_moves()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Wrapped.Join", "Library.Greeter");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var target = await ReadAsync(fixture, "Greeter.cs");

		// One tab for the member, two for the parameters it wrapped onto their own lines.
		target.ShouldContain(
			"\tpublic static string Join(\r\n\t\tstring first,\r\n\t\tstring second,\r\n\t\tstring third)\r\n\t{\r\n", Case.Sensitive);
	}

	/// <summary>
	/// An expression body wrapped across lines keeps its shape through a move, re-indented for the
	/// type it lands in rather than deepened or flattened by it. The <c>=&gt;</c> and the lines under
	/// it are continuations, which Roslyn's formatter has no rule about.
	/// </summary>
	[Test]
	public async Task Keeps_the_shape_of_a_wrapped_expression_body_it_moves()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Arrowed.Spread", "Library.Greeter");

		result.Applied.ShouldBeTrue("the move is written; only its layout is under test");
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var target = await ReadAsync(fixture, "Greeter.cs");

		// One tab for the member, two for the body, three for the lines it wraps onto.
		target.ShouldContain(
			"\tpublic static string Spread(string first, string second, string third) =>\r\n\t\tfirst"
				+ "\r\n\t\t\t+ \", \" + second\r\n\t\t\t+ \", \" + third;", Case.Sensitive);
	}

	/// <summary>
	/// A moved member arrives separated from the one above it, the same as one that is added.
	/// Roslyn's formatter reindents and moves braces but never inserts a blank line between members,
	/// so a member appended without one lands flush against the closing brace above it and no rule
	/// anywhere puts it back.
	/// </summary>
	[Test]
	public async Task Separates_the_member_it_moves_from_the_one_above_it()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Wrapped.Join", "Library.Greeter");

		result.Applied.ShouldBeTrue();

		var target = await ReadAsync(fixture, "Greeter.cs");

		target.ShouldContain("\t}\r\n\r\n\tpublic static string Join(", Case.Sensitive);
	}

	/// <summary>
	/// The other call-site style: the calls stay as written and each calling file imports the new
	/// home statically. Smaller diff, at the cost of a file whose calls no longer say where they go.
	/// </summary>
	[Test]
	public async Task Imports_the_new_home_instead_of_qualifying()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(
			session, "Library.Regioned.Twice", "Library.Greeter", CallSiteStyle.UsingStatic);

		result.Applied.ShouldBeTrue();
		result.Notices.ShouldContain(
			notice => notice.Contains("left as written", StringComparison.Ordinal));
	}

	/// <summary>
	/// An instance member's move changes what 'this' means inside it, and every call site would need
	/// a receiver it has no reason to have to hand. Refused rather than half done.
	/// </summary>
	[Test]
	public async Task Refuses_an_instance_member()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Greeter.cs");

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => MoveAsync(session, "Library.Greeter.Greet(string)", "Library.Regioned")).OfExactType();

		thrown.Message.ShouldContain("is an instance member", Case.Sensitive);
		thrown.Message.ShouldContain("Make it static first", Case.Sensitive);
		(await ReadAsync(fixture, "Greeter.cs")).ShouldBe(before);
	}

	/// <summary>Moving a member to where it already is is a mistake worth naming rather than a no-op.</summary>
	[Test]
	public async Task Refuses_a_move_to_the_type_it_is_already_in()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var thrown = await Should.ThrowAsync<ArgumentException>(
			() => MoveAsync(session, "Library.Regioned.Twice", "Library.Regioned")).OfExactType();

		thrown.Message.ShouldContain("is already in", Case.Sensitive);
	}

	/// <summary>
	/// An instance member nothing calls and that reads nothing of its type moves like a static one. A
	/// test method moving between fixtures is the shape: the runner finds it by attribute, so there is no
	/// call site to give a receiver, and its body means the same in either fixture. Its documentation
	/// comment and its attribute go with it.
	/// </summary>
	[Test]
	public async Task Moves_an_instance_member_nothing_calls_and_that_reads_nothing_of_its_type()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await WriteFixturesAsync(fixture);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Misplaced.Adds_two_numbers", "Library.Placed");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();
		result.Notices.ShouldContain(notice => notice.Contains("Moved as an instance member", StringComparison.Ordinal));
		result.Notices.ShouldNotContain(notice => notice.Contains("call site(s) now name", StringComparison.Ordinal));

		var text = await ReadAsync(fixture, "Fixtures.cs");
		var placed = text.IndexOf("class Placed", StringComparison.Ordinal);
		var moved = text.IndexOf("public void Adds_two_numbers()", StringComparison.Ordinal);

		moved.ShouldBeGreaterThan(placed);
		text.ShouldContain(
			"\t/// <summary>Adds two numbers, and reads nothing of the fixture.</summary>\r\n\t[Fact]\r\n"
				+ "\tpublic void Adds_two_numbers()", Case.Sensitive);
		text.IndexOf("Adds two numbers", StringComparison.Ordinal).ShouldBeGreaterThan(placed);
	}

	/// <summary>
	/// What a fixture inherits, the type it moves to inherits too, so a call through an implicit
	/// <c>this</c> to a member of their common base still reaches the same member.
	/// </summary>
	[Test]
	public async Task Moves_an_instance_member_that_uses_what_both_types_inherit()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await WriteFixturesAsync(fixture);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Misplaced.Uses_the_shared_base", "Library.Placed");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Fixtures.cs");

		text.IndexOf("Uses_the_shared_base", StringComparison.Ordinal)
			.ShouldBeGreaterThan(text.IndexOf("class Placed", StringComparison.Ordinal));
	}

	/// <summary>
	/// A call a member makes to itself is a reference, and one that goes where the member goes. It is
	/// not a call site to refuse over, nor one to rewrite.
	/// </summary>
	[Test]
	public async Task Moves_an_instance_member_that_calls_itself()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await WriteFixturesAsync(fixture);
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Misplaced.Counts_down", "Library.Placed");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldBeEmpty();

		var text = await ReadAsync(fixture, "Fixtures.cs");

		text.ShouldContain("remaining > 0 ? Counts_down(remaining - 1) : 0", Case.Sensitive);
		text.IndexOf("Counts_down", StringComparison.Ordinal)
			.ShouldBeGreaterThan(text.IndexOf("class Placed", StringComparison.Ordinal));
	}

	/// <summary>
	/// Every other instance member is refused, with what decided it, and nothing is written. Each row is
	/// one way a move would change what the member means: state of its own type, an explicit or captured
	/// <c>this</c>, a base the target does not share, a primary constructor parameter, a type parameter,
	/// a name that would bind to something else in the target, a name the target already answers to --
	/// its own, inherited, or an extension's, any of which existing calls could leave for the moved
	/// member -- a name the compiler binds by pattern, state a record's or a struct's equality covers,
	/// dispatch a reference search cannot see, a type that cannot take it, and a call site, implicit
	/// ones included.
	/// </summary>
	[Test]
	[Arguments("Library.Misplaced.Reads_the_seed", "Library.Placed", "reads Misplaced._seed at line")]
	[Arguments("Library.Misplaced.Calls_a_helper", "Library.Placed", "reads Misplaced.Helper at line")]
	[Arguments("Library.Misplaced.Names_this", "Library.Placed", "names 'this' at line")]
	[Arguments("Library.Misplaced.Captures_this", "Library.Placed", "reads Misplaced._seed at line")]
	[Arguments("Library.Misplaced.Uses_the_shared_base", "Library.Unrelated", "reads FixtureBase.Shared at line")]
	[Arguments("Library.Seeded.Reads_the_seed", "Library.Placed", "a parameter of Seeded's primary constructor")]
	[Arguments("Library.Boxed.Make", "Library.Placed", "a type parameter of Boxed")]
	[Arguments("Library.Misplaced.Uses_the_shared_base", "Library.Shadowing", "would mean Library.Shadowing.Shared()")]
	[Arguments("Library.Misplaced.Logs_a_note", "Library.Noting", "would mean Library.Noting.Note(object)")]
	[Arguments("Library.Misplaced.Close", "Library.Closing", "Closing already answers to Close, with Library.Closer.Close(), which it inherits")]
	[Arguments("Library.Misplaced.Log", "Library.Logger", "Logger already answers to Log, with Library.Logger.Log(object)")]
	[Arguments("Library.Misplaced.Trace", "Library.Logger", "Logger already answers to Trace, with the extension method Library.LoggerExtensions.Trace(")]
	[Arguments("Library.Misplaced.Deconstruct", "Library.Placed", "a name the compiler looks up by pattern")]
	[Arguments("Library.Bag.GetEnumerator", "Library.Placed", "with 1 reference(s), the first in BagUse.cs")]
	[Arguments("Library.Misplaced._spare", "Library.Ledger", "Ledger is a record, whose generated Equals, GetHashCode and ToString")]
	[Arguments("Library.Point.Spare", "Library.Placed", "Point is a struct, whose equality and layout")]
	[Arguments("Library.Misplaced.Names_base", "Library.Placed", "names 'base' at line")]
	[Arguments("Library.Misplaced.Adds_two_numbers", "Library.IRunnable", "IRunnable is an interface")]
	[Arguments("Library.Misplaced._left", "Library.Placed", "declared in one statement with others (_left, _right)")]
	[Arguments("Library.Misplaced.ToString", "Library.Placed", "is an override")]
	[Arguments("Library.Runner.Run", "Library.Placed", "implements Library.IRunnable.Run() for Running")]
	[Arguments("Library.Misplaced.Adds_two_numbers", "Library.Resettled", "Resettled is static")]
	[Arguments("Library.Misplaced.Is_called", "Library.Placed", "with 1 reference(s), the first in Fixtures.cs")]
	public async Task Refuses_an_instance_member_whose_move_would_change_what_it_means(
		string symbol,
		string targetType,
		string reason)
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await WriteFixturesAsync(fixture);
		await using var session = await TestSession.OpenAsync(fixture);

		var before = await ReadAsync(fixture, "Fixtures.cs");

		var thrown = await Should.ThrowAsync<ArgumentException>(() => MoveAsync(session, symbol, targetType)).OfExactType();

		thrown.Message.ShouldContain(reason, Case.Sensitive);
		(await ReadAsync(fixture, "Fixtures.cs")).ShouldBe(before);
	}

	/// <summary>
	/// Test fixtures, some of whose members could move and some of which could not. Written by the tests
	/// that use it rather than kept in the fixture, so no other test of the Members solution sees types
	/// it never asked for.
	/// </summary>
	private static async Task WriteFixturesAsync(FixtureSolution fixture)
	{
		await File.WriteAllTextAsync(
			fixture.Path("Members", "Library", "BagUse.cs"),
			"""
			namespace Library;

			/// <summary>Reaches Bag.GetEnumerator only through a foreach, which names it nowhere.</summary>
			public static class Emptying
			{
				public static int Sum(Bag bag)
				{
					var total = 0;

					foreach (var item in bag) total += item;

					return total;
				}
			}

			""".ReplaceLineEndings("\r\n"),
			TestContext.Current!.Execution.CancellationToken);

		await File.WriteAllTextAsync(
			fixture.Path("Members", "Library", "Fixtures.cs"),
			"""
			namespace Library;

			/// <summary>Marks a test, which a runner finds by reflection rather than calls.</summary>
			[AttributeUsage(AttributeTargets.Method)]
			public sealed class FactAttribute : Attribute
			{
			}

			/// <summary>What every fixture inherits.</summary>
			public abstract class FixtureBase
			{
				protected int Shared() => 1;
			}

			/// <summary>A fixture holding tests that belong in another.</summary>
			public sealed class Misplaced : FixtureBase
			{
				private readonly int _seed = 2;

				/// <summary>Adds two numbers, and reads nothing of the fixture.</summary>
				[Fact]
				public void Adds_two_numbers()
				{
					var sum = 1 + 2;
					Func<int, int> twice = value => value * 2;
					_ = twice(sum);
				}

				[Fact]
				public int Reads_the_seed() => _seed;

				[Fact]
				public int Calls_a_helper() => Helper();

				[Fact]
				public int Names_this() => this.GetHashCode();

				[Fact]
				public Func<int> Captures_this() => () => _seed + 1;

				[Fact]
				public int Uses_the_shared_base() => Shared() + 1;

				[Fact]
				public void Logs_a_note() => Note("hello");

				[Fact]
				public int Counts_down(int remaining) => remaining > 0 ? Counts_down(remaining - 1) : 0;

				public void Close()
				{
				}

				public int Is_called() => 3;

				public int Calls_it() => Is_called();

				public int Names_base() => base.GetHashCode();

				public void Log(string text) => Console.WriteLine(text);

				public void Trace(string text) => Console.WriteLine(text);

				public void Deconstruct(out int first, out int second) => (first, second) = (1, 2);

				private int _spare;

				private int _left = 1, _right = 2;

				public override string ToString() => "misplaced";

				private static void Note(string text) => Console.WriteLine(text);

				private int Helper() => _seed;
			}

			/// <summary>Where the misplaced tests belong.</summary>
			public sealed class Placed : FixtureBase
			{
				[Fact]
				public void Already_here()
				{
				}
			}

			/// <summary>A fixture that inherits nothing.</summary>
			public sealed class Unrelated
			{
			}

			/// <summary>A fixture whose own Shared hides the one every fixture inherits.</summary>
			public sealed class Shadowing : FixtureBase
			{
				private new int Shared() => 2;
			}

			/// <summary>A fixture with a Note of its own, which takes anything.</summary>
			public sealed class Noting
			{
				private static void Note(object value) => Console.WriteLine(value);
			}

			/// <summary>Something a fixture can close.</summary>
			public class Closer
			{
				public void Close()
				{
				}
			}

			/// <summary>A fixture that inherits a Close that a moved one would hide.</summary>
			public sealed class Closing : Closer
			{
			}

			/// <summary>Something that runs, called only through an interface.</summary>
			public interface IRunnable
			{
				void Run();
			}

			/// <summary>A Run that implements nothing itself, and that a derived type hands to an interface.</summary>
			public class Runner
			{
				public void Run()
				{
				}
			}

			/// <summary>Implements IRunnable with the Run it inherits.</summary>
			public sealed class Running : Runner, IRunnable
			{
			}

			/// <summary>A fixture whose state comes in through its primary constructor.</summary>
			public sealed class Seeded(int seed)
			{
				public int Reads_the_seed() => seed;
			}

			/// <summary>A fixture over a type it is given.</summary>
			public sealed class Boxed<T>
			{
				public T? Make() => default;
			}

			/// <summary>Logs anything, which a call passing a string reaches today.</summary>
			public sealed class Logger
			{
				public void Log(object value) => Console.WriteLine(value);
			}

			/// <summary>Traces through a Logger, for a call an instance Trace would take over.</summary>
			public static class LoggerExtensions
			{
				public static void Trace(this Logger logger, string text) => logger.Log(text);
			}

			/// <summary>Calls on a Logger that a moved Log or Trace would quietly take over.</summary>
			public static class Logging
			{
				public static void Run()
				{
					new Logger().Log("x");
					new Logger().Trace("y");
				}
			}

			/// <summary>Enumerable by pattern, which a foreach elsewhere reaches without naming.</summary>
			public sealed class Bag
			{
				public IEnumerator<int> GetEnumerator()
				{
					yield return 1;
				}
			}

			/// <summary>A record, whose equality takes in every field it holds.</summary>
			public sealed record Ledger(int Total);

			/// <summary>A struct, whose equality takes in every field it holds.</summary>
			public struct Point
			{
				public int Spare { get; set; }
			}

			""".ReplaceLineEndings("\r\n"),
			TestContext.Current!.Execution.CancellationToken);
	}

	private static Task<MemberEditResult> MoveAsync(
		WorkspaceSession session,
		string symbol,
		string targetType,
		CallSiteStyle callSites = CallSiteStyle.Qualify)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		var request = new MoveMemberRequest
		{
			Symbol = symbol,
			TargetType = targetType,
			CallSites = callSites,
		};

		return session.MutateAsync(
			(snapshot, token) => MoveMemberService.MoveAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	private static Task<string> ReadAsync(FixtureSolution fixture, params string[] parts) =>
		File.ReadAllTextAsync(
			fixture.Path("Members", "Library", string.Join(Path.DirectorySeparatorChar, parts)),
			TestContext.Current!.Execution.CancellationToken);

	/// <summary>
	/// A move that lands somewhere missing an import says which namespace would fix it. This is the
	/// tool that needs that line most: it writes to a file the caller never named, so the caller has
	/// no reason to have thought about that file's imports at all.
	/// <para>
	/// <c>Imports.Formatted</c> names <c>CultureInfo</c> and its own file imports
	/// <c>System.Globalization</c>; <c>Greeter</c>'s file imports nothing. So the move carries the
	/// code and leaves the using behind, which is the whole shape of the problem.
	/// </para>
	/// <para>
	/// The compilation has already been built to work out what the move broke, so naming the
	/// namespace costs nothing beyond asking it something it can already answer.
	/// </para>
	/// </summary>
	[Test]
	public async Task Names_the_namespace_a_move_left_behind()
	{
		using var fixture = FixtureSolution.Copy("Members", "Members.slnx");
		await using var session = await TestSession.OpenAsync(fixture);

		var result = await MoveAsync(session, "Library.Imports.Formatted", "Library.Greeter");

		result.Applied.ShouldBeTrue();
		result.IntroducedDiagnostics.ShouldNotBeEmpty();

		var said = string.Join(" ", result.Notices);

		said.ShouldContain("System.Globalization", Case.Sensitive);
		said.ShouldContain("This introduced", Case.Sensitive);
	}
}
