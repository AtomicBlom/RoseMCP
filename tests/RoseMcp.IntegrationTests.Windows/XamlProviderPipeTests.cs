using System.IO.Pipes;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.LiveApp.Xaml;

namespace RoseMcp.IntegrationTests.Windows;

/// <summary>
/// The channel every XAML verb rides, driven directly rather than through a target app.
/// <para>
/// A live-app test cannot reach this. The provider lives inside somebody else's process, so nothing
/// in the suite can drop the far end of a pipe the host owns -- which is why the reconnect path was
/// unverified for as long as it existed. Here the far end is a <c>NamedPipeClientStream</c> in this
/// process and dropping it is one <c>Dispose</c>.
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

		Assert.Null(pipe.Listen());

		// The host waits on a thread of its own, which is not a nicety: the pipe is created with no
		// output buffer, so a client that writes before the server has accepted blocks in its write
		// until somebody reads. Two real processes never notice, because the host is not the thing
		// that is blocked; one thread driving both ends stops dead, measured at the write.
		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));

		using var provider = await ConnectAsync(pipe.Name);
		await SendFrameAsync(provider, "ready");

		Assert.Equal("ready", await waiting);
		Assert.True(pipe.Connected);
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
	/// </summary>
	[Test]
	public async Task Listens_again_for_a_provider_that_reconnects_after_the_first_one_goes()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		Assert.Null(pipe.Listen());

		(await GreetAsync(pipe, "ready")).Dispose();

		// The provider is gone, and nothing told the host -- exactly as when a target's tap dies.
		using var second = await ConnectAsync(pipe.Name);
		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));
		await SendFrameAsync(second, "ready again");

		Assert.Equal("ready again", await waiting);
		Assert.True(pipe.Connected);
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
		Assert.Null(pipe.Listen());

		(await GreetAsync(pipe, "ready")).Dispose();

		using var second = await ConnectAsync(pipe.Name);
		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));
		await SendFrameAsync(second, "ready again");
		Assert.Equal("ready again", await waiting);

		// Answering on a thread of its own, because Request writes and then blocks on the reply.
		var answering = Task.Run(async () =>
		{
			Assert.Equal("tree", await ReadFrameAsync(second));
			await SendFrameAsync(second, "one\ttwo");
		});

		Assert.Equal("one\ttwo", pipe.Request("tree", Patience));
		await answering;
	}

	/// <summary>
	/// A host with nobody on the far end says so by returning null rather than waiting forever. The
	/// bound is what turns "the app is wedged" into a call that comes back and names the channel.
	/// </summary>
	[Test]
	public void Gives_up_on_a_provider_that_never_connects()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);
		Assert.Null(pipe.Listen());

		Assert.Null(pipe.WaitForProvider(TimeSpan.FromMilliseconds(250)));
		Assert.False(pipe.Connected);
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

	/// <summary>A pipe names itself before anything connects, which needs no I/O at all.</summary>
	[Test]
	public void Names_itself_after_the_host_that_owns_it()
	{
		using var pipe = new XamlProviderPipe(NullLogger.Instance);

		Assert.StartsWith($"rosemcp-xaml-{Environment.ProcessId}-", pipe.Name, StringComparison.Ordinal);
	}

	/// <summary>
	/// One provider connecting and greeting the host, handed back so the caller decides when it goes
	/// away. The wait goes on a thread of its own before anything dials, for the reason the first test
	/// records.
	/// </summary>
	/// <param name="pipe">The listening host.</param>
	/// <param name="greeting">What the provider says when it arrives.</param>
	private static async Task<NamedPipeClientStream> GreetAsync(XamlProviderPipe pipe, string greeting)
	{
		var waiting = Task.Run(() => pipe.WaitForProvider(Patience));

		var provider = await ConnectAsync(pipe.Name);
		await SendFrameAsync(provider, greeting);

		Assert.Equal(greeting, await waiting);

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
		Assert.Null(pipe.Listen());

		var provider = await GreetAsync(pipe, "ready");
		Assert.True(pipe.Connected);

		provider.Dispose();
		await Task.Delay(250);

		Assert.False(pipe.Connected, "a host that still believes a departed provider is connected never hangs up and never listens again");
	}
}
