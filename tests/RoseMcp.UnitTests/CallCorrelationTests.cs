using System.ComponentModel;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoseMcp.Broker;
using RoseMcp.Logging;

namespace RoseMcp.UnitTests;

/// <summary>
/// One call, one id, in every process it crosses -- and only for as long as the call lasts.
/// <para>
/// The failure these are about is a log that answers the wrong question confidently: a line filed
/// under a call it was not written for, because the id leaked into a loop the call happened to
/// start, or an id a client chose, written verbatim into every line of a file a person reads.
/// </para>
/// </summary>
public sealed class CallCorrelationTests
{
	private const string Sent = "0123456789ab";

	[Test]
	public void A_call_that_arrives_without_an_id_is_given_a_fresh_one()
	{
		using (CallCorrelation.Begin(null))
		{
			CallCorrelation.IsWellFormed(CallCorrelation.Id).ShouldBeTrue();
		}

		CallCorrelation.Id.ShouldBeNull();
	}

	[Test]
	public void A_call_takes_the_id_the_previous_hop_sent()
	{
		using var call = CallCorrelation.Begin(Meta(Sent));

		CallCorrelation.Id.ShouldBe(Sent);
	}

	/// <summary>
	/// What is read here is written into every line of the call, so anything but the shape a Rose
	/// process mints -- a newline that would forge a line, an id long enough to bury the message, a
	/// value that is not a string at all -- is replaced rather than repeated.
	/// </summary>
	[Test]
	[Arguments("0123456789AB")]
	[Arguments("0123456\n2026-01-01 00:00:00.000Z [ERR] forged")]
	[Arguments("0123456")]
	[Arguments("0123456789abcdef0123456789abcdef0")]
	[Arguments("0123-456789ab")]
	[Arguments("")]
	public void An_id_that_is_not_the_shape_Rose_mints_is_replaced(string sent)
	{
		CallCorrelation.Read(Meta(sent)).ShouldBeNull();

		using var call = CallCorrelation.Begin(Meta(sent));

		CallCorrelation.Id.ShouldNotBe(sent);
		CallCorrelation.IsWellFormed(CallCorrelation.Id).ShouldBeTrue();
	}

	[Test]
	public void An_id_that_is_not_a_string_is_replaced()
	{
		CallCorrelation.Read(new JsonObject { [CallCorrelation.MetaKey] = 12345678 }).ShouldBeNull();
		CallCorrelation.Read(new JsonObject { [CallCorrelation.MetaKey] = new JsonObject() }).ShouldBeNull();
	}

	[Test]
	public void Every_minted_id_is_one_a_later_hop_accepts()
	{
		var minted = Enumerable.Range(0, 100).Select(_ => CallCorrelation.Mint()).ToList();

		minted.ShouldAllBe(id => CallCorrelation.IsWellFormed(id));
		minted.Distinct().Count().ShouldBe(minted.Count);
	}

	[Test]
	public void Ending_a_call_restores_the_one_around_it()
	{
		using var outer = CallCorrelation.Begin(Meta(Sent));

		using (CallCorrelation.Begin(null))
		{
			CallCorrelation.Id.ShouldNotBe(Sent);
		}

		CallCorrelation.Id.ShouldBe(Sent);
	}

	/// <summary>
	/// Work a call starts inherits its execution context, and a loop started by the first call of the
	/// day would otherwise file its lines under that call until the process exits.
	/// </summary>
	[Test]
	public async Task Work_a_call_started_stops_carrying_its_id_when_the_call_ends()
	{
		var readDuring = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var callEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<string?> started;

		using (CallCorrelation.Begin(Meta(Sent)))
		{
			started = Task.Run(async () =>
			{
				readDuring.SetResult(CallCorrelation.Id);
				await callEnded.Task;
				return CallCorrelation.Id;
			});

			(await readDuring.Task).ShouldBe(Sent);
		}

		callEnded.SetResult();

		(await started).ShouldBeNull();
	}

	[Test]
	public async Task Concurrent_calls_each_see_their_own_id()
	{
		var bothStarted = new Barrier(2);

		var ids = await Task.WhenAll(
			Task.Run(() => Within("aaaaaaaaaaaa")),
			Task.Run(() => Within("bbbbbbbbbbbb")));

		ids.ShouldBe(["aaaaaaaaaaaa", "bbbbbbbbbbbb"]);

		string? Within(string id)
		{
			using var call = CallCorrelation.Begin(Meta(id));
			bothStarted.SignalAndWait(TimeSpan.FromSeconds(10));
			return CallCorrelation.Id;
		}
	}

	/// <summary>
	/// A poll loop, a sweep or a child's transport started from inside a call is started detached, so
	/// it inherits none of the call's ambient state -- not even while the call is still running.
	/// </summary>
	[Test]
	public async Task Detached_work_inherits_nothing_from_the_call_that_started_it()
	{
		using var call = CallCorrelation.Begin(Meta(Sent));
		using var origin = CallOrigin.Use(Path.GetTempPath());

		var seen = await Detached.Run(() => Task.FromResult((CallCorrelation.Id, CallOrigin.Directory)));

		seen.ShouldBe((null, null));
		CallCorrelation.Id.ShouldBe(Sent);
	}

	/// <summary>
	/// The hop itself, through a real server and the SDK's own transport: the far side's ambient is
	/// set from what crossed the wire and nothing else, since the two ends share no execution context.
	/// </summary>
	[Test]
	public async Task Every_internal_hop_carries_the_id_of_the_call_it_is_made_for(CancellationToken cancellationToken)
	{
		await using var connection = await OpenAsync(cancellationToken);

		using var call = CallCorrelation.Begin(Meta(Sent));

		var result = await CancellableToolCall.InvokeAsync(
			connection.Client, "probe_correlation", new Dictionary<string, object?>(), progress: null, cancellationToken);

		Text(result).ShouldBe(Sent);
	}

	/// <summary>
	/// A broker with no relay in front of it is the first Rose process its calls reach, so it mints;
	/// and a client that sends something malformed gets a fresh id rather than its own text in the log.
	/// </summary>
	[Test]
	[Arguments(null)]
	[Arguments("not\nan id")]
	public async Task The_first_Rose_process_a_call_reaches_gives_it_an_id(string? sent, CancellationToken cancellationToken)
	{
		await using var connection = await OpenAsync(cancellationToken);

		var result = await connection.Client.CallToolAsync(
			new CallToolRequestParams { Name = "probe_correlation", Meta = sent is null ? null : Meta(sent) },
			cancellationToken);

		CallCorrelation.IsWellFormed(Text(result)).ShouldBeTrue();
	}

	private static JsonObject Meta(string id) => new() { [CallCorrelation.MetaKey] = id };

	private static string Text(CallToolResult result) =>
		result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;

	private static Task<IgnoredArgumentsTests.Connection> OpenAsync(CancellationToken cancellationToken) =>
		IgnoredArgumentsTests.Connection.OpenAsync(
			services => services.AddMcpServer().WithTools<Probe>().WithCallCorrelation(),
			cancellationToken);

	/// <summary>Answers with the id the call is running under, as the far side of a hop sees it.</summary>
	[McpServerToolType]
	public sealed class Probe
	{
		[McpServerTool(Name = "probe_correlation")]
		[Description("Says which call this is.")]
		public static string Correlation() => CallCorrelation.Id ?? "none";
	}
}
