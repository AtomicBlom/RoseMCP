using System.Text.Json;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The two processes a relayed session is made of: an http broker standing in for the tray, and a
/// stdio server pointed at it.
/// <para>
/// This is the only shape in which the relay can be observed at all. Everything that makes it worth
/// having is a property of two processes rather than of either -- that the stdio session starts no
/// workers of its own, that the directory its client launched it in reaches a broker which could
/// never learn it otherwise, and what a call says once the tray behind it has gone.
/// </para>
/// <para>
/// The broker is a child process rather than a host built inside the test, and the difference is
/// not incidental. What these tests do to it is kill it, because that is what a deploy does to a
/// tray; an in-process host can only be asked to stop, which is the one shutdown a session never
/// meets in practice. It also keeps ASP.NET Core out of this test project.
/// </para>
/// <para>
/// No environment variable was needed to point the session at it: <c>--port</c> already chooses
/// both the address an http broker binds and the one a stdio session probes for a tray.
/// </para>
/// </summary>
public sealed class RelayFixture : IAsyncDisposable
{
	private RelayFixture(int port, RoseServerProcess broker, RoseServerProcess session)
	{
		Port = port;
		Broker = broker;
		Session = session;
	}

	public int Port { get; }

	/// <summary>The http broker, standing in for a tray. Killing it is what a deploy looks like.</summary>
	public RoseServerProcess Broker { get; private set; }

	/// <summary>The stdio session in front of it, which declares no tools of its own.</summary>
	public RoseServerProcess Session { get; }

	/// <summary>
	/// Starts both, with the session's working directory set to <paramref name="workingDirectory"/> --
	/// which is the one fact a stdio process has and an http broker cannot get any other way.
	/// </summary>
	public static async Task<RelayFixture> StartAsync(string workingDirectory, CancellationToken cancellationToken)
	{
		var port = RoseServerProcess.FreePort();
		var broker = RoseServerProcess.Start("--transport", "http", "--port", port.ToString());

		try
		{
			await WaitForBrokerAsync(port, cancellationToken);
		}
		catch
		{
			broker.Dispose();
			throw;
		}

		var session = RoseServerProcess.StartIn(workingDirectory, "--port", port.ToString());

		try
		{
			await session.InitializeAsync(cancellationToken);
		}
		catch
		{
			session.Dispose();
			broker.Dispose();
			throw;
		}

		return new RelayFixture(port, broker, session);
	}

	/// <summary>
	/// Kills the broker and waits for the port to stop answering, which is what a deploy does to a
	/// tray mid-session. Waited for rather than assumed: a killed process releases its listener a
	/// moment later, and a test that races that is testing the timing rather than the relay.
	/// </summary>
	public async Task StopBrokerAsync(CancellationToken cancellationToken)
	{
		Broker.Dispose();

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (DateTime.UtcNow < deadline)
		{
			if (!await IsAnsweringAsync(Port, cancellationToken)) return;

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		throw new InvalidOperationException($"The broker on port {Port} went on answering after it was killed.");
	}

	/// <summary>Brings a broker back on the same port, which is the second half of a deploy.</summary>
	public async Task RestartBrokerAsync(CancellationToken cancellationToken)
	{
		Broker = RoseServerProcess.Start("--transport", "http", "--port", Port.ToString());
		await WaitForBrokerAsync(Port, cancellationToken);
	}

	/// <summary>What the broker itself says it has loaded, which is how a test learns who served a call.</summary>
	public async Task<JsonDocument> DescribeWorkspacesAsync(CancellationToken cancellationToken)
	{
		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{Port}/admin/workspaces"), cancellationToken);

		return JsonDocument.Parse(body);
	}

	private static async Task WaitForBrokerAsync(int port, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
		while (DateTime.UtcNow < deadline)
		{
			if (await IsAnsweringAsync(port, cancellationToken)) return;

			await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		}

		throw new InvalidOperationException($"The http broker never answered on port {port}.");
	}

	private static async Task<bool> IsAnsweringAsync(int port, CancellationToken cancellationToken)
	{
		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
			using var answer = await client.GetAsync(
				new Uri($"http://127.0.0.1:{port}/admin/workspaces"), cancellationToken);

			return answer.IsSuccessStatusCode;
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
	}

	public ValueTask DisposeAsync()
	{
		Session.Dispose();
		Broker.Dispose();

		return ValueTask.CompletedTask;
	}
}
