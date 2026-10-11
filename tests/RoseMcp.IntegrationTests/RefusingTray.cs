using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// A tray that answers the probe a stdio session makes before deciding how to serve itself, and
/// refuses everything after it: the session believes a tray is there and then cannot open MCP to it.
/// <para>
/// A raw socket rather than an http server, because all it says is two fixed replies and the test
/// project keeps ASP.NET Core out. It answers on a thread of its own, not the thread pool: the
/// session probes with a two-second budget, and a suite running tests side by side keeps the pool
/// busy enough with copies and process waits that a pooled accept loop misses it, and the session
/// rightly decides there is no tray. Every request line it reads is kept, so a test can tell a
/// session that tried the tray and moved on from one that never tried it.
/// </para>
/// </summary>
public sealed class RefusingTray : IDisposable
{
	private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
	private readonly Thread _answering;

	public RefusingTray()
	{
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

		_answering = new Thread(Answer) { IsBackground = true, Name = "RefusingTray" };
		_answering.Start();
	}

	public int Port { get; }

	/// <summary>The request line of every request read, as "GET /admin/workspaces".</summary>
	public ConcurrentQueue<string> Requests { get; } = new();

	public void Dispose()
	{
		_listener.Stop();
		_answering.Join(TimeSpan.FromSeconds(5));
	}

	private void Answer()
	{
		while (true)
		{
			Socket client;
			try
			{
				client = _listener.AcceptSocket();
			}
			catch (Exception)
			{
				// Stopping the listener ends the wait for a connection by throwing, which is how it was asked to end.
				return;
			}

			using (client)
			{
				try
				{
					client.ReceiveTimeout = 5000;

					var request = RequestOf(ReadHead(client));
					Requests.Enqueue(request);

					var reply = request == "GET /admin/workspaces"
						? "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]"
						: "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

					client.Send(Encoding.ASCII.GetBytes(reply));
					client.Shutdown(SocketShutdown.Send);
				}
				catch (Exception)
				{
					// A client that goes before its answer is the client's business; this tray refuses it either way.
				}
			}
		}
	}

	/// <summary>The method and path of a request head, as "GET /admin/workspaces".</summary>
	private static string RequestOf(string head)
	{
		var parts = head.Split("\r\n", 2)[0].Split(' ');

		return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : head;
	}

	/// <summary>Reads up to the blank line ending the request's headers; a body, if any, is left unread.</summary>
	private static string ReadHead(Socket client)
	{
		var head = new StringBuilder();
		var buffer = new byte[1];

		while (head.Length < 16 * 1024)
		{
			if (client.Receive(buffer) == 0) break;

			head.Append((char)buffer[0]);

			var ended = head.Length >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r';
			if (ended) break;
		}

		return head.ToString();
	}
}
