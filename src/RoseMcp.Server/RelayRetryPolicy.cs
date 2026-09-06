using ModelContextProtocol.Protocol;

namespace RoseMcp.Server;

/// <summary>
/// Which forwarded calls may be re-issued after the connection to the tray dies.
/// <para>
/// Not all of them, which is what the broker already knows and the relay did not. WorkspaceManager
/// retries only where the caller passed <c>retryIfWorkerDied</c>, and every writing tool passes
/// false. The relay retried anything, on the reasoning that a transport failure means the tray
/// restarted and the first call never ran -- but a broken socket with the tray alive is a transport
/// failure too, and then the rename was applied and the retry applies it again. A
/// position-addressed rename re-run after the identifier changed length lands on a different token.
/// </para>
/// <para>
/// The tray's own <c>tools/list</c> carries the answer in each tool's read-only annotation, and the
/// relay forwards that list already, so nothing new crosses the wire. Until it has arrived nothing
/// is retried but the listing itself: a tool the list has not covered might be a rename, and one
/// clear failure the caller can repeat is cheaper than a second edit nobody asked for.
/// </para>
/// </summary>
public sealed class RelayRetryPolicy
{
	private volatile IReadOnlyDictionary<string, bool>? _readOnly;

	/// <summary>Whether the tray's tool list has been seen, so a decision is more than a default.</summary>
	public bool Known => _readOnly is not null;

	/// <summary>Records what the tray says about its own tools. Replaces what was known, never merges.</summary>
	public void Learn(IEnumerable<Tool> tools) =>
		_readOnly = tools
			.GroupBy(tool => tool.Name, StringComparer.Ordinal)
			.ToDictionary(
				group => group.Key,
				group => group.First().Annotations?.ReadOnlyHint == true,
				StringComparer.Ordinal);

	/// <summary>
	/// Whether re-sending this tool is safe. Unknown is no, for both a tool absent from the list and
	/// a list that has not arrived.
	/// </summary>
	public bool MayRetry(string tool) => _readOnly?.GetValueOrDefault(tool) == true;
}
