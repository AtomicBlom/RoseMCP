using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

using RoseMcp.Contracts;
using RoseMcp.Logging;

namespace RoseMcp.Broker;

/// <summary>
/// One tool call being recorded in the <see cref="ActivityRecord"/>, from the filter that saw it arrive to the
/// answer it got, and what the code it reached on the way said about it.
/// <para>
/// Ambient for the length of the call, for the reason the correlation id is: the facts worth keeping are known
/// at different depths -- the client and the arguments at the filter, the workspace or debug session where the
/// call is routed, a write's diff before the broker shapes it for the caller -- and threading a parameter
/// through every tool to carry them is how one tool added later would go unrecorded. So the filter makes one,
/// and the places a call is routed through add to it if there is one: <see cref="Routed"/> from a worker or a
/// live-app session, <see cref="Wrote"/> from the step every write passes. Where there is none -- an operator
/// route, a poll loop, a sweep -- nothing is recorded, which is what keeps a window's own reads and the broker's
/// own work out of the record.
/// </para>
/// <para>
/// It names a call only while that call lasts. Work a call starts inherits its execution context and with it
/// this ambient, so ending the call ends it for everything that inherited it, and a loop started by the first
/// call of the day cannot write into that call's entry hours later. Recording never throws: it runs on every
/// call, and a record of a call is not a reason for the call to fail.
/// </para>
/// </summary>
public sealed class CallRecording : IDisposable
{
	private const int MaxName = 128;

	private const int MaxPath = 1024;
	private static readonly AsyncLocal<CallRecording?> Ambient = new();

	private readonly ActivityRecord _record;
	private readonly CallRecording? _previous;
	private readonly Lock _gate = new();

	private readonly string? _relayed;
	private readonly string? _transportSession;
	private readonly string? _transportClientName;

	private readonly List<ActivityFileChange> _changes = [];
	private readonly List<string> _notes = [];

	private int _diffBudget = ActivityBodies.CallDiffCeiling;
	private volatile bool _over;
	private bool _finished;

	private string? _workspace;
	private string? _workspaceKey;
	private string? _debugSession;
	private string? _target;
	private bool? _applied;

	internal CallRecording(
		ActivityRecord record,
		string? tool,
		IDictionary<string, JsonElement>? arguments,
		string? relayed,
		string? transportSession,
		string? transportClientName,
		string? origin)
	{
		_record = record;
		_relayed = relayed;
		_transportSession = string.IsNullOrWhiteSpace(transportSession) ? null : transportSession;
		_transportClientName = transportClientName;

		Id = ActivityRecord.MintId();
		StartedUtc = record.Now;
		// Both from the client, and shown to a person: a tool the broker does not have is still called by name.
		Tool = ConnectedClients.Clean(tool, MaxName);
		Origin = ConnectedClients.Clean(origin, MaxPath);
		CorrelationId = CallCorrelation.Id;
		ClientKey = KeyOf(relayed, _transportSession);

		var (kept, fullLength) = ActivityBodies.Cut(Safely(() => ActivityBodies.Arguments(arguments)), ActivityBodies.ArgumentsCeiling);
		Arguments = kept;
		ArgumentsFullLength = fullLength;
		if (fullLength is { } full) _notes.Add(ActivityBodies.CutNote("arguments", full, kept!.Length));

		_previous = Ambient.Value;
		Ambient.Value = this;
	}

	/// <summary>The call being recorded on this execution context, or null outside one, and once it has ended.</summary>
	public static CallRecording? Current => Ambient.Value is { _over: false } recording ? recording : null;

	/// <summary>The entry's id.</summary>
	public string Id { get; }

	/// <summary>When the call arrived, on the broker's clock.</summary>
	public DateTimeOffset StartedUtc { get; }

	/// <summary>
	/// The tool called, as the client named it, cleaned of anything unprintable and cut to a length.
	/// </summary>
	public string? Tool { get; }

	/// <summary>
	/// The directory the calling session works in, as its relay sent it; null where it sent none.
	/// </summary>
	public string? Origin { get; }

	/// <summary>The call's correlation id, which every log line written for it carries.</summary>
	public string? CorrelationId { get; }

	/// <summary>See <see cref="ActivityEntry.ClientKey"/>.</summary>
	public string ClientKey { get; }

	/// <summary>
	/// The arguments as the client sent them, in JSON, cut to
	/// <see cref="ActivityBodies.ArgumentsCeiling"/>.
	/// </summary>
	public string? Arguments { get; }

	/// <summary>
	/// How long the arguments really are, where <see cref="Arguments"/> was cut; null otherwise.
	/// </summary>
	public int? ArgumentsFullLength { get; }

	/// <summary>
	/// <paramref name="relayed"/> and <paramref name="transportSession"/> as one short hash: the key the client
	/// table files the client under, hashed, so the record can say which client across a broker restart without
	/// carrying the session's name, which scopes what that session acknowledges.
	/// </summary>
	public static string KeyOf(string? relayed, string? transportSession)
	{
		var key = ConnectedClients.KeyOf(relayed, string.IsNullOrWhiteSpace(transportSession) ? null : transportSession);

		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 8));
	}

	/// <summary>
	/// Says where the call was routed: the workspace or the debug session that serves it, and what it is aimed
	/// at. The first of each that is said is kept, since the call's own routing comes before anything the
	/// routing does for itself, such as a worker's first status read.
	/// </summary>
	public void Routed(string? workspace = null, string? workspaceKey = null, string? debugSession = null, string? target = null)
	{
		lock (_gate)
		{
			if (_finished) return;

			if (_workspace is null && workspace is not null)
			{
				_workspace = workspace;
				_workspaceKey = workspaceKey;
			}

			_debugSession ??= debugSession;
			_target ??= target;
		}
	}

	/// <summary>
	/// Keeps a write's files and each one's diff, from the result as the worker gave it, before the broker
	/// leaves the diff off and names the files relative for the caller.
	/// </summary>
	public void Wrote(WorkspaceMutationResult result)
	{
		try
		{
			lock (_gate)
			{
				if (_finished) return;

				_applied = _applied is null ? result.Applied : _applied | result.Applied;
				_changes.AddRange(ActivityBodies.Changes(result.ChangedFiles, result.Diff, ref _diffBudget, _notes));
			}
		}
		catch (Exception exception)
		{
			_record.Failed(exception);
		}
	}

	/// <summary>Records the call as answered with <paramref name="result"/>: failed where the result is an error, succeeded otherwise.</summary>
	public void Finish(CallToolResult result)
	{
		var failed = result.IsError == true;

		Complete(failed ? ActivityOutcome.Failed : ActivityOutcome.Succeeded, failed ? Safely(() => ActivityBodies.Error(result)) : null, result);
	}

	/// <summary>Records the call as having failed with what the caller was told.</summary>
	public void Fail(string? error) => Complete(ActivityOutcome.Failed, error, result: null);

	/// <summary>Records the call as given up by its caller, or by the broker going.</summary>
	public void Cancel() => Complete(ActivityOutcome.Cancelled, error: null, result: null);

	/// <summary>
	/// The entry as it stands, for a call still running or one just finished. <paramref name="outcome"/> is how
	/// it ended, or <see cref="ActivityOutcome.Running"/>.
	/// </summary>
	internal ActivityEntry Snapshot(
		ActivityOutcome outcome,
		DateTimeOffset? finishedUtc = null,
		string? error = null,
		CallToolResult? result = null)
	{
		var client = _record.Clients.Of(_relayed, _transportSession);
		var structured = result?.StructuredContent;

		var response = result is null ? null : Safely(() => ActivityBodies.Response(result));
		var (kept, fullLength) = ActivityBodies.Cut(response, ActivityBodies.ResponseCeiling);

		lock (_gate)
		{
			var notes = new List<string>(_notes);
			if (fullLength is { } full) notes.Add(ActivityBodies.CutNote("response", full, kept!.Length));

			return new ActivityEntry
			{
				Id = Id,
				Kind = ActivityEntryKind.Call,
				Broker = _record.Broker,
				StartedUtc = StartedUtc,
				FinishedUtc = finishedUtc,
				CorrelationId = CorrelationId,
				ClientId = client?.Id,
				ClientKey = ClientKey,
				ClientName = client?.ClientName ?? (_relayed is null ? _transportClientName : null),
				ClientLabel = client?.Label,
				Origin = Origin,
				Workspace = _workspace ?? Text(structured, "workspace"),
				WorkspaceKey = _workspaceKey ?? Text(structured, "workspaceKey"),
				DebugSession = _debugSession ?? Text(structured, "sessionId"),
				Tool = Tool,
				Target = _target,
				Outcome = outcome,
				Error = error,
				Revision = Number(structured, "revision"),
				Applied = _applied,
				Arguments = Arguments,
				ArgumentsFullLength = ArgumentsFullLength,
				Response = kept,
				ResponseFullLength = fullLength,
				Changes = [.. _changes],
				Notes = notes,
			};
		}
	}

	/// <summary>Ends the call for everything that inherited it, recording it as cancelled where nothing said how it ended.</summary>
	public void Dispose()
	{
		_over = true;
		Ambient.Value = _previous;

		Cancel();
	}

	private void Complete(ActivityOutcome outcome, string? error, CallToolResult? result)
	{
		try
		{
			lock (_gate)
			{
				if (_finished) return;
			}

			var (said, fullLength) = ActivityBodies.Cut(error, ActivityBodies.ErrorCeiling);
			var entry = Snapshot(outcome, _record.Now, said, result);

			if (fullLength is { } full)
			{
				entry = entry with
				{
					ErrorFullLength = full,
					Notes = [.. entry.Notes, ActivityBodies.CutNote("error", full, said!.Length)],
				};
			}

			lock (_gate)
			{
				if (_finished) return;

				_finished = true;
			}

			_record.Finished(this, entry);
		}
		catch (Exception exception)
		{
			_record.Failed(exception);
			_record.Forget(this);
		}
	}

	private T? Safely<T>(Func<T?> read)
	{
		try
		{
			return read();
		}
		catch (Exception exception)
		{
			_record.Failed(exception);
			return default;
		}
	}

	private static string? Text(JsonElement? structured, string property) =>
		structured is { ValueKind: JsonValueKind.Object } json
			&& json.TryGetProperty(property, out var value)
			&& value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

	private static long? Number(JsonElement? structured, string property) =>
		structured is { ValueKind: JsonValueKind.Object } json
			&& json.TryGetProperty(property, out var value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt64(out var number)
				? number
				: null;
}
