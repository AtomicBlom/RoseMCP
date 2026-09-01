using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Calls a tool on another MCP endpoint and tells that endpoint when the caller gives up.
/// <para>
/// McpClient.CallToolAsync honours a cancellation token locally: it stops waiting and throws. It
/// does not send notifications/cancelled, so the far side never learns, and here the far side is a
/// worker holding a solution. A cancelled diagnostics run was measured finishing its analyzer pass
/// four seconds after the caller had gone -- and because reads on a workspace are ordered behind
/// one another, that abandoned work is not merely wasted CPU, it is the delay before the next call
/// on that workspace can start.
/// </para>
/// <para>
/// Sending the notification needs the request id, and CallToolAsync never reveals the one it
/// chose. So the request is built here instead. The progress token is chosen here too, and
/// notifications carrying it are matched back to the caller's IProgress, which is the part
/// CallToolAsync was otherwise doing for us.
/// </para>
/// </summary>
public static class CancellableToolCall
{
	public static async Task<CallToolResult> InvokeAsync(
		McpClient client,
		string tool,
		IReadOnlyDictionary<string, object?> arguments,
		IProgress<ProgressNotificationValue>? progress,
		CancellationToken cancellationToken)
	{
		var requestId = new RequestId(Guid.NewGuid().ToString("N"));
		var progressToken = new ProgressToken(requestId.ToString());

		var parameters = new CallToolRequestParams
		{
			Name = tool,
			Arguments = arguments.ToDictionary(
				pair => pair.Key,
				pair => JsonSerializer.SerializeToElement(pair.Value, ContractJson.Options)),
			// ProgressToken is read-only, derived from _meta, so the token goes in there directly.
			Meta = progress is null
				? null
				: new JsonObject { ["progressToken"] = JsonValue.Create(progressToken.ToString()) },
		};

		await using var listening = progress is null
			? null
			: client.RegisterNotificationHandler(
				NotificationMethods.ProgressNotification,
				(notification, _) =>
				{
					Forward(notification, progressToken, progress);
					return default;
				});

		// Tell the far side first, and only then stop waiting. Over http the request is a streaming
		// POST, so cancelling the wait tears the connection down -- and a teardown three
		// milliseconds ahead of the notification was enough for the far side to treat the request as
		// simply gone, never cancel its own token, and let the work run to completion. Notifying
		// first goes out on its own connection while the request is still alive, which is what makes
		// the far side cancel rather than merely lose interest.
		using var abandon = new CancellationTokenSource();

		using var cancelling = cancellationToken.Register(() => _ = NotifyThenAbandonAsync());

		try
		{
			return await SendAsync(abandon.Token);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw new OperationCanceledException(cancellationToken);
		}

		async Task NotifyThenAbandonAsync()
		{
			try
			{
				await client.SendNotificationAsync(
					NotificationMethods.CancelledNotification,
					new CancelledNotificationParams { RequestId = requestId },
					cancellationToken: CancellationToken.None);
			}
			catch (Exception)
			{
				// The far side may already be gone, which is the one case where not telling it is fine.
			}

			await abandon.CancelAsync();
		}

		async Task<CallToolResult> SendAsync(CancellationToken token)
		{
			var response = await client.SendRequestAsync(
				new JsonRpcRequest
				{
					Id = requestId,
					Method = RequestMethods.ToolsCall,
					Params = JsonSerializer.SerializeToNode(parameters, ContractJson.Options),
				},
				token);

			return response.Result.Deserialize<CallToolResult>(ContractJson.Options)
				?? throw new InvalidOperationException($"The response to {tool} could not be read.");
		}
	}

	private static void Forward(
		JsonRpcNotification notification,
		ProgressToken token,
		IProgress<ProgressNotificationValue> progress)
	{
		var reported = notification.Params?.Deserialize<ProgressNotificationParams>(ContractJson.Options);
		if (reported is null || !reported.ProgressToken.Equals(token)) return;

		progress.Report(reported.Progress);
	}
}
