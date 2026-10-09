using RoseMcp.Contracts;
using RoseMcp.LiveApp.Debugging;

namespace RoseMcp.IntegrationTests.Windows;

/// <summary>
/// A page of debug events cut at its limit says what lies past it, and every group it names is a value
/// a filter takes: one exception among a page of module loads is seen to be there and asked for by its
/// type, rather than paged to.
/// </summary>
public sealed class DebugEventBufferTests
{
	[Test]
	public void A_page_cut_at_its_limit_counts_what_lies_past_it_by_kind_and_exception_type()
	{
		var buffer = new DebugEventBuffer();

		for (var index = 0; index < 5; index++)
		{
			buffer.Append(LiveDebugEventKind.ModuleLoaded, $"module {index}", moduleName: $"M{index}.dll");
		}

		buffer.Append(LiveDebugEventKind.ExceptionFirstChance, "thrown", exceptionType: "System.InvalidOperationException");
		buffer.Append(LiveDebugEventKind.ExceptionFirstChance, "thrown", exceptionType: "System.IO.IOException");

		var page = buffer.ReadAfter(0, limit: 3);

		page.Events.Count.ShouldBe(3);
		var beyond = page.Beyond.ShouldNotBeNull();
		beyond.Total.ShouldBe(4);
		beyond.Kinds.Select(group => (group.Kind, group.Count))
			.ShouldBe([(nameof(LiveDebugEventKind.ExceptionFirstChance), 2), (nameof(LiveDebugEventKind.ModuleLoaded), 2)]);
		beyond.ExceptionTypes.Select(group => group.ExceptionType)
			.ShouldBe(["System.InvalidOperationException", "System.IO.IOException"], ignoreOrder: true);

		var asked = buffer.ReadAfter(0, exceptionType: "InvalidOperationException");

		asked.Events.ShouldHaveSingleItem().ExceptionType.ShouldBe("System.InvalidOperationException");
		asked.Beyond.ShouldBeNull("a page holding every match has nothing past it");
		asked.NextCursor.ShouldBe(7, "the cursor still advances over what the filter passed over");
	}

	[Test]
	public async Task A_wait_for_one_exception_type_is_not_answered_by_another()
	{
		var buffer = new DebugEventBuffer();
		buffer.Append(LiveDebugEventKind.ExceptionFirstChance, "thrown", exceptionType: "System.IO.IOException");

		var wait = buffer.WaitForAsync(
			0,
			[LiveDebugEventKind.ExceptionFirstChance],
			TimeSpan.FromSeconds(10),
			TestContext.Current!.Execution.CancellationToken,
			exceptionType: "System.InvalidOperationException");

		buffer.Append(LiveDebugEventKind.ExceptionFirstChance, "thrown", exceptionType: "System.InvalidOperationException");

		(await wait).ShouldBe(DebugEventWait.Arrived);
	}
}
