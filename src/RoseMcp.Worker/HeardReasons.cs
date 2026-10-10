using System.Text.Json;
using System.Text.Json.Nodes;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// How the call in flight hears each kind of reason for each project, as the broker sent it under
/// <see cref="ReasonHearing.MetaKey"/>, for the length of that call.
/// <para>
/// Ambient rather than a tool argument, for the reason the correlation id is: every read passes the snapshot's
/// notices on, and a parameter threaded through every read tool is a parameter the next read tool forgets. It
/// is read in one place, <see cref="WorkspaceHost.ReadAsync"/>, where the degraded notice is composed.
/// </para>
/// <para>
/// Read leniently. A call that sends nothing, or something that does not parse, hears every reason as an
/// unaccepted one, which is what a read said before anybody accepted anything: the failure that costs a line
/// the caller did not need, never the one that hides a reason it did.
/// </para>
/// </summary>
public static class HeardReasons
{
	private static readonly AsyncLocal<IReadOnlyList<ReasonHearing>?> Ambient = new();

	/// <summary>What the call in flight was sent; empty outside a call, or for a call that was sent nothing.</summary>
	public static IReadOnlyList<ReasonHearing> Current => Ambient.Value ?? [];

	/// <summary>Takes what <paramref name="meta"/> carries for the length of a call. Dispose it when the call ends.</summary>
	public static IDisposable Begin(JsonObject? meta) => new Scope(Read(meta));

	/// <summary>The hearings <paramref name="meta"/> carries, or none where it carries none or nothing readable.</summary>
	public static IReadOnlyList<ReasonHearing> Read(JsonObject? meta)
	{
		if (meta?[ReasonHearing.MetaKey] is not JsonArray list) return [];

		try
		{
			var read = list.Deserialize<ReasonHearing?[]>(ContractJson.Options) ?? [];

			return [.. read.OfType<ReasonHearing>().Where(hearing => !string.IsNullOrEmpty(hearing.Kind))];
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
		{
			return [];
		}
	}

	private sealed class Scope : IDisposable
	{
		private readonly IReadOnlyList<ReasonHearing>? _previous;

		public Scope(IReadOnlyList<ReasonHearing> heard)
		{
			_previous = Ambient.Value;
			Ambient.Value = heard;
		}

		public void Dispose() => Ambient.Value = _previous;
	}
}
