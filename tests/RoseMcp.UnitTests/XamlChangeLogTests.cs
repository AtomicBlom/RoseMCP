using RoseMcp.Broker;
using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The record of what live edits changed in a debug session's app, which a window marks and filters on: only an
/// edit that took, who made it, what it was before and after, bounded, and named by the client table as it is now.
/// </summary>
public sealed class XamlChangeLogTests
{
	private static readonly DateTime Now = new(2026, 10, 12, 14, 5, 0, DateTimeKind.Utc);

	private static LiveXamlApplyResult Applied(params LiveXamlEditResult[] results) =>
		new() { Results = results, Applied = results.Count(result => result.Status == "applied") };

	private static LiveXamlEditResult Margin(string status = "applied") => new()
	{
		Kind = "SetProperty",
		Target = "#ToolStrip",
		Property = "Margin",
		Value = "8,0,8,0",
		Status = status,
		Handle = 4242,
		OldValue = "12,0,12,0",
	};

	/// <summary>An edit that did not take changed nothing, so it is not a change anybody should be shown.</summary>
	[Test]
	public void Only_an_edit_that_took_is_recorded_with_its_element_and_both_values()
	{
		var log = new XamlChangeLog();

		log.Record(Applied(Margin(), Margin("property not found")), new LiveAppActor("agent-1"), "D:/app/Ink.xaml", Now)
			.ShouldBe(1);

		var (changes, dropped) = log.Snapshot();
		dropped.ShouldBe(0);
		var change = changes.ShouldHaveSingleItem().Change;

		change.Sequence.ShouldBe(1);
		change.ChangedUtc.ShouldBe(Now);
		change.Person.ShouldBeFalse();
		change.Tool.ShouldBe(ToolNames.XamlApply);
		change.Handle.ShouldBe(4242UL);
		change.Property.ShouldBe("Margin");
		change.OldValue.ShouldBe("12,0,12,0");
		change.NewValue.ShouldBe("8,0,8,0");
		change.File.ShouldBe("D:/app/Ink.xaml");
	}

	/// <summary>A cleared property has a before and no after: what it falls back to is the framework's, not the edit's.</summary>
	[Test]
	public void A_cleared_property_has_no_new_value()
	{
		var log = new XamlChangeLog();

		log.Record(Applied(Margin() with { Kind = "ClearProperty", Value = null }), LiveAppActor.Person, file: null, Now);

		var change = log.Snapshot().Changes.ShouldHaveSingleItem();
		change.Change.NewValue.ShouldBeNull();
		change.Change.OldValue.ShouldBe("12,0,12,0");
		change.Change.Person.ShouldBeTrue();
		change.By.IsPerson.ShouldBeTrue();
	}

	/// <summary>
	/// Bounded, oldest first out, and saying how many went: an agent in an edit loop makes several changes a minute
	/// and a session can run all day, while a list that starts part way through must not read as everything.
	/// </summary>
	[Test]
	public void The_oldest_changes_go_first_and_the_count_let_go_is_kept()
	{
		var log = new XamlChangeLog();
		var many = Enumerable.Range(0, XamlChangeLog.Capacity + 7).Select(_ => Margin()).ToArray();

		log.Record(Applied(many), new LiveAppActor("agent-1"), file: null, Now);

		var (changes, dropped) = log.Snapshot();
		changes.Count.ShouldBe(XamlChangeLog.Capacity);
		dropped.ShouldBe(7);
		changes[0].Change.Sequence.ShouldBe(8);
		changes[^1].Change.Sequence.ShouldBe(XamlChangeLog.Capacity + 7);
	}

	/// <summary>A window waiting on the list is answered as a change lands, and an apply that changed nothing wakes nobody.</summary>
	[Test]
	public void A_recorded_change_wakes_a_waiting_window_and_a_failed_apply_does_not()
	{
		var log = new XamlChangeLog();

		var waiting = log.Changed.Next;
		log.Record(Applied(Margin("SetProperty failed 0x80004005")), new LiveAppActor("agent-1"), file: null, Now);
		waiting.IsCompleted.ShouldBeFalse();

		log.Record(Applied(Margin()), new LiveAppActor("agent-1"), file: null, Now);
		waiting.IsCompleted.ShouldBeTrue();
	}

	/// <summary>
	/// Changes are applied from calls that run side by side, so two batches recorded at once keep every change and
	/// number them without a gap or a repeat.
	/// </summary>
	[Test]
	public async Task Changes_recorded_at_once_are_all_kept_and_numbered_once_each()
	{
		var log = new XamlChangeLog();

		await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
		{
			for (var batch = 0; batch < 20; batch++)
			{
				log.Record(Applied(Margin(), Margin()), new LiveAppActor("agent-1"), file: null, Now);
			}
		})));

		var sequences = log.Snapshot().Changes.Select(entry => entry.Change.Sequence).ToList();
		sequences.Count.ShouldBe(320);
		sequences.ShouldBe(Enumerable.Range(1, 320).Select(sequence => (long)sequence).ToList());
	}

	/// <summary>
	/// A window names the agent session that made a change by its row in the client table now, and names nobody for a
	/// session the table no longer lists; the version moves when another change lands and not otherwise.
	/// </summary>
	[Test]
	public void A_window_reads_who_made_each_change_by_the_client_table_now()
	{
		var log = new XamlChangeLog();
		log.Record(Applied(Margin()), new LiveAppActor("key-1"), file: null, Now);
		log.Record(Applied(Margin()), new LiveAppActor("key-gone"), file: null, Now);
		log.Record(Applied(Margin()), LiveAppActor.Person, file: null, Now);

		var present = new Dictionary<string, ClientSummary>
		{
			["key-1"] = new() { Id = "c-1", Label = "D:\\Drawboard\\PDF", Named = true, ConnectedUtc = Now },
		};

		var list = OperatorViews.Changes("session-1", log, present);

		list.SessionId.ShouldBe("session-1");
		list.Changes.Count.ShouldBe(3);
		list.Changes[0].Agent.ShouldNotBeNull().ClientId.ShouldBe("c-1");
		list.Changes[0].Agent!.Name.ShouldBe("D:\\Drawboard\\PDF");
		list.Changes[1].Agent.ShouldBeNull();
		list.Changes[1].Person.ShouldBeFalse();
		list.Changes[2].Agent.ShouldBeNull();
		list.Changes[2].Person.ShouldBeTrue();

		OperatorViews.Changes("session-1", log, present).Version.ShouldBe(list.Version);

		log.Record(Applied(Margin()), new LiveAppActor("key-1"), file: null, Now);
		OperatorViews.Changes("session-1", log, present).Version.ShouldNotBe(list.Version);
	}

	/// <summary>
	/// An agent's live edit through a session is recorded against that agent session, which is how a window can say
	/// who changed an element when the host that applied it never learned who asked.
	/// </summary>
	[Test]
	public async Task An_agents_apply_through_a_session_is_recorded_against_that_agent()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;

		await using var hosts = new FakeLiveAppHost { ApplyAnswer = Applied(Margin()) };
		await using var sessions = new LiveAppSessionManager(
			Microsoft.Extensions.Options.Options.Create(new BrokerOptions { LiveAppHost = hosts.For, AppUserModelIdOf = _ => null }),
			Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
			Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveAppSessionManager>.Instance);

		var session = await sessions.StartAsync(
			new LiveAppTarget { Kind = LiveAppTargetKind.AttachProcess, ProcessId = 4545, Description = "probe" },
			LiveAppOwners.Agent("mcp-one"),
			cancellationToken);

		string? caller;
		using (CallSession.Use("mcp-one"))
		{
			caller = CallSession.ClientKey;
			await session.ApplyXamlAsync(oldXaml: "<a/>", newXaml: "<b/>", filePath: null, cancellationToken);
		}

		var recorded = session.XamlChanges.Snapshot().Changes.ShouldHaveSingleItem();
		recorded.By.IsPerson.ShouldBeFalse();
		recorded.By.ClientKey.ShouldBe(caller);
		recorded.Change.Handle.ShouldBe(4242UL);
	}
}
