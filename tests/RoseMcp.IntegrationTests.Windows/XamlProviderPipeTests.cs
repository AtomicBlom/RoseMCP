using System.IO.Pipes;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;
using RoseMcp.LiveApp.Xaml;

namespace RoseMcp.IntegrationTests.Windows;

/// <summary>
/// The channel every XAML verb rides, driven directly rather than through a target app.
/// <para>
/// A live-app test cannot reach this. The provider lives inside somebody else's process, so nothing
/// in the suite can drop the far end of a pipe the host owns -- which is why the reconnect path was
/// unverified for as long as it existed. Here the far end is a <c>NamedPipeClientStream</c> in this
/// process and dropping it is one <c>Dispose</c>. For the same reason this is where a provider can be
/// made to answer late, or greet as a stale copy would: both need the far end to misbehave on cue,
/// which a real provider in a real app will not do.
/// </para>
/// </summary>
public sealed class XamlProviderPipeTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

	/// <summary>
	/// A provider connects, sends its greeting, and the host reads it. The ordinary first connection,
	/// and the baseline the reconnect case is measured against.
	/// </summary>
	[Test]
	public async Task Reads_the_greeting_of_a_provider_that_connects()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);

		pipe.Listen().ShouldBeNull();

		// The host waits on a thread of its own, which is not a nicety: the pipe is created with no
		// output buffer, so a client that writes before the server has accepted blocks in its write
		// until somebody reads. Two real processes never notice, because the host is not the thing
		// that is blocked; one thread driving both ends stops dead, measured at the write.
		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));

		using var provider = await ConnectAsync(pipe.Name);
		await SendFrameAsync(provider, XamlWire.Greeting(pipe.Nonce));

		(await waiting).ShouldBe(XamlWire.Greeting(pipe.Nonce));
		pipe.Connected.ShouldBeTrue();
		pipe.Refused.ShouldBeNull();
	}

	/// <summary>
	/// A provider that has been and gone leaves the stream refusing the next connection until it is
	/// disconnected, so the host hangs up before listening again.
	/// <para>
	/// Without that, a session whose pipe drops is over: the host re-injects, a fresh provider dials
	/// the same name, and nothing is listening -- which reads as a provider that failed to load rather
	/// than as a channel that was never reopened. That is the recovery a dropped pipe depends on, and
	/// this is the only place it can be made to happen: the provider lives inside somebody else's
	/// process, so no live-app test can drop the far end.
	/// </para>
	/// <para>
	/// Both providers greet alike, because a re-injected one is given the same key. So the wait is
	/// shown to be pending until the second greets: a host that handed back the departed provider's
	/// greeting would answer it at once, and take a dead tap for a live one.
	/// </para>
	/// </summary>
	[Test]
	public async Task Listens_again_for_a_provider_that_reconnects_after_the_first_one_goes()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		(await GreetAsync(pipe)).Dispose();

		// The provider is gone, and nothing told the host -- exactly as when a target's tap dies.
		using var second = await ConnectAsync(pipe.Name);
		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));

		await Task.Delay(200);
		waiting.IsCompleted.ShouldBeFalse("the wait was answered before the second provider greeted");

		await SendFrameAsync(second, XamlWire.Greeting(pipe.Nonce));

		(await waiting).ShouldBe(XamlWire.Greeting(pipe.Nonce));
		pipe.Connected.ShouldBeTrue();
	}

	/// <summary>
	/// And the reconnected provider serves requests, which is the half that makes the reconnect worth
	/// anything: a channel that accepts a connection and then cannot carry a verb has recovered
	/// nothing.
	/// </summary>
	[Test]
	public async Task Serves_a_request_over_the_reconnected_pipe()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		(await GreetAsync(pipe)).Dispose();
		using var second = await GreetAsync(pipe);

		// Answering on a thread of its own, because Request writes and then blocks on the reply.
		var answering = Task.Run(async () =>
		{
			var (id, request) = await ReadRequestAsync(second);
			request.ShouldBe("tree");
			await SendFrameAsync(second, XamlWire.Frame(id, "one\ttwo"));
		});

		pipe.Request("tree", Patience).ShouldBe("one\ttwo");
		await answering;
	}

	/// <summary>
	/// The reply to a request the host gave up on is not the answer to the next one. The provider
	/// serves on the app's UI thread, which the host cannot cancel, so a request that timed out is
	/// still answered -- later, and ahead of the reply to whatever the host asked next. That frame is
	/// well formed and answers a different question; read by position, the next request would take it
	/// as its own, and every answer after that would be one behind.
	/// </summary>
	[Test]
	public async Task Drops_a_late_reply_by_its_id_and_answers_the_next_request_with_its_own()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		using var provider = await GreetAsync(pipe);
		var gaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		// A provider that serves in order, as the real one does, and is slow with the first request:
		// it answers only once the host has stopped waiting for it.
		var serving = Task.Run(async () =>
		{
			var (first, _) = await ReadRequestAsync(provider);
			await gaveUp.Task;
			await SendFrameAsync(provider, XamlWire.Frame(first, "the tree, late"));

			var (second, request) = await ReadRequestAsync(provider);
			request.ShouldBe("selection");
			await SendFrameAsync(provider, XamlWire.Frame(second, "the selection"));
		});

		pipe.Request("tree", TimeSpan.FromMilliseconds(300)).ShouldBeNull();
		gaveUp.SetResult();

		pipe.Request("selection", Patience).ShouldBe("the selection");
		await serving;
	}

	/// <summary>
	/// The mismatch the protocol version exists for. The host and the provider ship together, so the
	/// way two versions meet is a stale copy, and one that greets without a version is older than the
	/// host. Refused before anything is asked of it, since it would read rows it was not written for
	/// and answer with data -- and said as a refusal, because from outside it looks exactly like a
	/// provider that never connected.
	/// </summary>
	[Test]
	public async Task Refuses_a_provider_older_than_the_host_and_says_why()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));

		using var provider = await ConnectAsync(pipe.Name);
		await SendFrameAsync(provider, XamlWire.UnversionedGreeting);

		// Released as soon as the greeting is refused, rather than at the bound.
		var answered = await Task.WhenAny(waiting, Task.Delay(Patience / 2));
		answered.ShouldBeSameAs(waiting);
		(await waiting).ShouldBeNull();

		pipe.Connected.ShouldBeFalse();
		pipe.Refused.ShouldNotBeNull();
		pipe.Refused.ShouldContain("older than this host", Case.Sensitive);
	}

	/// <summary>
	/// The pipe grants every packaged app on the machine, so whatever reaches it first greets first. A
	/// greeting in the right shape without the key this session issued is not the provider it injected.
	/// </summary>
	[Test]
	public async Task Refuses_a_provider_that_does_not_present_the_sessions_key()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));

		using var provider = await ConnectAsync(pipe.Name);
		await SendFrameAsync(provider, XamlWire.Greeting("not-this-sessions-key"));

		(await waiting).ShouldBeNull();
		pipe.Connected.ShouldBeFalse();
		pipe.Refused!.ShouldContain("key", Case.Sensitive);
	}

	/// <summary>
	/// Refusing a provider hangs up on it and listens again, so something that reached the pipe first
	/// cannot keep the provider this session injected from connecting after it -- and the refusal is
	/// forgotten once a provider is accepted, or a working session would go on reporting it.
	/// </summary>
	[Test]
	public async Task Accepts_the_right_provider_after_refusing_one()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		var refused = Task.Run(() => pipe.WaitForProvider(Patience));
		using (var impostor = await ConnectAsync(pipe.Name))
		{
			await SendFrameAsync(impostor, XamlWire.Greeting("not-this-sessions-key"));
			(await refused).ShouldBeNull();
		}

		using var provider = await GreetAsync(pipe);

		pipe.Connected.ShouldBeTrue();
		pipe.Refused.ShouldBeNull();
	}

	/// <summary>
	/// A host with nobody on the far end says so by returning null rather than waiting forever. The
	/// bound is what turns "the app is wedged" into a call that comes back and names the channel.
	/// </summary>
	[Test]
	public void Gives_up_on_a_provider_that_never_connects()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		pipe.WaitForProvider(TimeSpan.FromMilliseconds(250)).ShouldBeNull();
		pipe.Connected.ShouldBeFalse();
		pipe.Refused.ShouldBeNull();
	}

	private static async Task<NamedPipeClientStream> ConnectAsync(string name)
	{
		var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
		await client.ConnectAsync((int)Patience.TotalMilliseconds);

		return client;
	}

	/// <summary>One length-prefixed UTF-8 message, which is the whole framing.</summary>
	private static async Task SendFrameAsync(Stream stream, string payload)
	{
		var bytes = Encoding.UTF8.GetBytes(payload);
		var header = new byte[4];
		header[0] = (byte)(bytes.Length & 0xFF);
		header[1] = (byte)((bytes.Length >> 8) & 0xFF);
		header[2] = (byte)((bytes.Length >> 16) & 0xFF);
		header[3] = (byte)((bytes.Length >> 24) & 0xFF);

		await stream.WriteAsync(header);
		await stream.WriteAsync(bytes);
		await stream.FlushAsync();
	}

	private static async Task<string> ReadFrameAsync(Stream stream)
	{
		var header = new byte[4];
		await stream.ReadExactlyAsync(header);

		var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
		var payload = new byte[length];
		await stream.ReadExactlyAsync(payload);

		return Encoding.UTF8.GetString(payload);
	}

	/// <summary>A request as the host sends it: the id to answer under, and the request itself.</summary>
	private static async Task<(uint Id, string Request)> ReadRequestAsync(Stream stream)
	{
		var frame = await ReadFrameAsync(stream);
		XamlWire.TryReadFrame(frame, out var id, out var request).ShouldBeTrue($"a request with no id: '{frame}'");

		return (id, request);
	}

	/// <summary>A pipe names itself before anything connects, which needs no I/O at all.</summary>
	[Test]
	public void Names_itself_after_the_host_that_owns_it()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);

		pipe.Name.ShouldStartWith($"rosemcp-xaml-{Environment.ProcessId}-", Case.Sensitive);
	}

	/// <summary>
	/// One provider connecting and greeting the host as the provider this session injected does,
	/// handed back so the caller decides when it goes away. The wait goes on a thread of its own before
	/// anything dials, for the reason the first test records.
	/// </summary>
	/// <param name="pipe">The listening host.</param>
	private static async Task<NamedPipeClientStream> GreetAsync(XamlProviderPipe pipe)
	{
		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));

		var provider = await ConnectAsync(pipe.Name);
		await SendFrameAsync(provider, XamlWire.Greeting(pipe.Nonce));

		(await waiting).ShouldBe(XamlWire.Greeting(pipe.Nonce));

		return provider;
	}

	/// <summary>
	/// What the host believes about a provider that has gone. A server pipe's <c>IsConnected</c> is
	/// its own state rather than the far end's, so it stays true after the client closes -- and every
	/// decision the reconnect turns on is asked of it.
	/// </summary>
	[Test]
	public async Task Reports_whether_it_still_believes_a_departed_provider_is_connected()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		pipe.Listen().ShouldBeNull();

		var provider = await GreetAsync(pipe);
		pipe.Connected.ShouldBeTrue();

		provider.Dispose();
		await Task.Delay(250);

		pipe.Connected.ShouldBeFalse("a host that still believes a departed provider is connected never hangs up and never listens again");
	}
}
