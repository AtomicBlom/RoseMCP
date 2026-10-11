using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

namespace RoseMcp.Broker;

/// <summary>
/// Hands an agent session the notes a person has sent it, on the answer to its next call of any tool.
/// <para>
/// A filter, for the reason the activity record is one: every call passes through it, so no tool can be added that
/// a note fails to ride on. It sits inside the activity record's filter, so the entry of the call that carried a
/// note holds the note in its answer and the note names that entry, and outside every other, so the notices the
/// ignored-argument filter adds come first and the person's notes last.
/// </para>
/// <para>
/// Only an answer delivers. A call that is refused or fails reaches its client as an error the SDK writes once
/// every filter has finished, with nowhere to put a note, and a cancelled call's answer is not one its client is
/// waiting for; either way the notes wait for the next call. They are claimed only once the answer is in hand, so
/// exactly one answer carries each, and two calls answered together never both do. A relay's own read is not one of
/// its session's calls and carries nothing; listings are not calls and are not filtered here at all.
/// </para>
/// </summary>
public static class WaitingNotes
{
	/// <summary>Registers the filter. See the class summary for where it belongs in the pipeline.</summary>
	internal static IMcpServerBuilder WithWaitingNotes(this IMcpServerBuilder builder) =>
		builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
		{
			var parameters = context.Params;
			var server = context.Server;
			var notes = CallSession.IsRelayRead(parameters?.Meta) ? null : server.Services?.GetService<AgentNotes>();

			if (notes is null) return await next(context, cancellationToken);

			// The relay's name read with no transport session in the way, as the client table reads it: a note is held
			// under the key the table files its client under.
			var relayed = CallSession.Of(null, parameters?.Meta);
			var transportSession = server.SessionId;

			using var caller = AgentNotes.UseCaller(relayed, transportSession);

			var result = await next(context, cancellationToken);

			var answered = result.IsError != true && !cancellationToken.IsCancellationRequested;
			if (!answered) return result;

			var logger = server.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(WaitingNotes));

			return Carry(notes, result, relayed, transportSession, parameters?.Name, CallRecording.Current?.Id, logger);
		}));

	/// <summary>
	/// <paramref name="result"/> with every note waiting for its client added to its notices, each claimed for this
	/// answer. A result the notices cannot be added to gives its notes back, to wait for the next answer, and is
	/// answered as it was: a fault in carrying a note is no reason to fail a call that worked.
	/// </summary>
	private static CallToolResult Carry(
		AgentNotes notes,
		CallToolResult result,
		string? relayed,
		string? transportSession,
		string? tool,
		string? callId,
		ILogger? logger)
	{
		var claim = notes.Claim(relayed, transportSession, tool, callId);
		if (claim.Notes.Count == 0) return result;

		try
		{
			return IgnoredArguments.Noticed(result, [.. claim.Notes.Select(AgentNotes.Notice)]);
		}
		catch (Exception exception)
		{
			notes.Release(claim);
			logger?.LogWarning(exception, "Notes could not ride on the answer to {Tool}; they wait for the next one.", tool);

			return result;
		}
	}
}
