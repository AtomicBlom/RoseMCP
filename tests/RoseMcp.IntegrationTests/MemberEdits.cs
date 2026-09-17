using Microsoft.Extensions.Logging.Abstractions;

using RoseMcp.Contracts;

namespace RoseMcp.IntegrationTests;

/// <summary>
/// How a member-edit test asks for an edit and reads back what it did to the file.
/// <para>
/// Shared rather than copied into each suite. Every one of them needs the same four calls, and the
/// shape of <see cref="EditAsync"/> is the part worth having in one place: an edit goes through
/// <c>MutateAsync</c> with the session's own self-write note, which is what keeps the workspace from
/// treating the edit as a change somebody else made on disk. A copy that lost that would still pass
/// its own assertions and would be testing a different thing.
/// </para>
/// </summary>
internal static class MemberEdits
{
	internal static Task<MemberEditResult> ReplaceAsync(WorkspaceSession session, string symbol, string code) =>
		EditAsync(session, Request(MemberEditKind.Replace, symbol, code));

	internal static MemberEditRequest Request(MemberEditKind kind, string symbol, string code) =>
		new() { Kind = kind, Symbol = symbol, Code = code };

	internal static Task<MemberEditResult> EditAsync(WorkspaceSession session, MemberEditRequest request)
	{
		var diagnostics = new DiagnosticsService(NullLogger<DiagnosticsService>.Instance);

		return session.MutateAsync(
			(snapshot, token) => MemberEditService.EditAsync(
				snapshot, diagnostics, request, session.NoteSelfWrite, token),
			TestContext.Current!.Execution.CancellationToken);
	}

	internal static Task<string> ReadAsync(FixtureSolution fixture, string file) =>
		File.ReadAllTextAsync(fixture.Path("Members", "Library", file), TestContext.Current!.Execution.CancellationToken);
}
